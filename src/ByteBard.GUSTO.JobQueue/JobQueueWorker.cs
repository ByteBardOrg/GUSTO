namespace ByteBard.GUSTO;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

public class JobQueueWorker<TStorageRecord> : BackgroundService
    where TStorageRecord : class, IJobStorageRecord
{
    private static JsonSerializerSettings _settings = new JsonSerializerSettings
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting = Formatting.None
    };

    private static readonly ActivitySource ActivitySource = new(GustoTelemetry.ActivitySourceName);
    private static readonly Meter Meter = new(GustoTelemetry.MeterName);

    private static readonly Counter<long> JobsProcessedCounter = Meter.CreateCounter<long>(
        "gusto.jobs.processed",
        description: "Total number of jobs processed successfully");

    private static readonly Counter<long> JobsFailedCounter = Meter.CreateCounter<long>(
        "gusto.jobs.failed",
        description: "Total number of jobs that failed");

    private static readonly Histogram<double> JobExecutionDuration = Meter.CreateHistogram<double>(
        "gusto.job.duration",
        unit: "ms",
        description: "Job execution duration in milliseconds");

    private static readonly Histogram<double> BatchProcessingDuration = Meter.CreateHistogram<double>(
        "gusto.batch.duration",
        unit: "ms",
        description: "Batch processing duration in milliseconds");

    private static readonly Histogram<long> BatchSizeHistogram = Meter.CreateHistogram<long>(
        "gusto.batch.size",
        description: "Number of jobs in each batch");

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JobQueueWorker<TStorageRecord>> _logger;
    private readonly GustoConfig _config;

    /// <summary>
    /// Test hook: When set, the worker will pause at the start of each batch cycle and wait for this TaskCompletionSource to be signaled.
    /// This allows tests to enqueue jobs before the worker processes them. The barrier is automatically cleared after being awaited.
    /// </summary>
    /// <example>
    /// <code>
    /// JobQueueWorker&lt;MyRecord&gt;.BatchStartBarrier = new TaskCompletionSource();
    /// await jobQueue.EnqueueAsync(() => myService.DoWork());
    /// JobQueueWorker&lt;MyRecord&gt;.BatchStartBarrier.SetResult(); // Let worker proceed
    /// </code>
    /// </example>
    public static TaskCompletionSource? BatchStartBarrier { get; set; }

    /// <summary>
    /// Test hook: When set, the worker will signal this TaskCompletionSource after completing a batch of jobs.
    /// This allows tests to await job completion before asserting results. The barrier is automatically cleared after being signaled.
    /// </summary>
    /// <example>
    /// <code>
    /// JobQueueWorker&lt;MyRecord&gt;.BatchCompletedBarrier = new TaskCompletionSource();
    /// JobQueueWorker&lt;MyRecord&gt;.BatchStartBarrier.SetResult();
    /// await JobQueueWorker&lt;MyRecord&gt;.BatchCompletedBarrier.Task; // Wait for completion
    /// // Assert results here
    /// </code>
    /// </example>
    public static TaskCompletionSource? BatchCompletedBarrier { get; set; }

    public JobQueueWorker(
        IServiceProvider serviceProvider,
        IOptions<GustoConfig> config,
        ILogger<JobQueueWorker<TStorageRecord>> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobQueueWorker started.");

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = _config.Concurrency,
            CancellationToken = stoppingToken
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WaitForStartBarrierIfSet();
                await ProcessBatchCycleAsync(parallelOptions, stoppingToken);
                SignalBatchCompletedIfSet();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure in JobQueueWorker.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("JobQueueWorker stopped.");
    }

    private static async Task WaitForStartBarrierIfSet()
    {
        if (BatchStartBarrier != null)
        {
            await BatchStartBarrier.Task;
            BatchStartBarrier = null;
        }
    }
    
    private static void SignalBatchCompletedIfSet()
    {
        BatchCompletedBarrier?.TrySetResult();
        BatchCompletedBarrier = null;
    }

    private async Task ProcessBatchCycleAsync(ParallelOptions parallelOptions, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IJobStorageProvider<TStorageRecord>>();

        var jobs = await FetchPendingJobsAsync(storage, stoppingToken);
        var jobStorageRecords = jobs.ToList();

        if (!jobStorageRecords.Any())
        {
            await Task.Delay(_config.PollInterval, stoppingToken);
            return;
        }

        await ProcessBatchAsync(jobStorageRecords, storage, scope.ServiceProvider, parallelOptions);
    }

    private async Task<IEnumerable<TStorageRecord>> FetchPendingJobsAsync(
        IJobStorageProvider<TStorageRecord> storage,
        CancellationToken stoppingToken)
    {
        return await storage.GetBatchAsync(
            new JobSearchParams<TStorageRecord>
            {
                Match = job =>
                    !job.IsComplete &&
                    job.ExecuteAfter <= DateTime.UtcNow &&
                    (job.ExpireOn == null || job.ExpireOn > DateTime.UtcNow),
                Limit = _config.BatchSize,
                CancellationToken = stoppingToken
            }, stoppingToken);
    }

    private async Task ProcessBatchAsync(
        List<TStorageRecord> jobStorageRecords,
        IJobStorageProvider<TStorageRecord> storage,
        IServiceProvider scopedServiceProvider,
        ParallelOptions parallelOptions)
    {
        using var batchActivity = ActivitySource.StartActivity("ProcessBatch");
        batchActivity?.SetTag("batch.size", jobStorageRecords.Count);

        var batchStopwatch = Stopwatch.StartNew();
        BatchSizeHistogram.Record(jobStorageRecords.Count);

        await Parallel.ForEachAsync(jobStorageRecords, parallelOptions, async (storedJob, ct) =>
        {
            await ExecuteJobAsync(storedJob, storage, scopedServiceProvider, ct);
        });

        batchStopwatch.Stop();
        BatchProcessingDuration.Record(batchStopwatch.Elapsed.TotalMilliseconds);
        batchActivity?.SetStatus(ActivityStatusCode.Ok);
    }

    private async Task ExecuteJobAsync(
        TStorageRecord storedJob,
        IJobStorageProvider<TStorageRecord> storage,
        IServiceProvider scopedServiceProvider,
        CancellationToken ct)
    {
        using var jobActivity = ActivitySource.StartActivity("ExecuteJob");
        jobActivity?.SetTag("job.tracking_id", storedJob.TrackingId);
        jobActivity?.SetTag("job.type", storedJob.JobType);
        jobActivity?.SetTag("job.method", storedJob.MethodName);

        var jobStopwatch = Stopwatch.StartNew();
        try
        {
            var jobType = Type.GetType(storedJob.JobType);
            var arguments = JsonConvert.DeserializeObject<object[]>(storedJob.ArgumentsJson, _settings);
            var jobInstance = ActivatorUtilities.CreateInstance(scopedServiceProvider, jobType);
            var method = jobType.GetMethod(storedJob.MethodName);
            await (Task)method.Invoke(jobInstance, arguments);
            
            await storage.MarkJobAsCompleteAsync(storedJob, ct);
            RecordJobSuccess(storedJob, jobStopwatch, jobActivity);
        }
        catch (Exception ex)
        {
            await RecordJobFailureAsync(storedJob, storage, ex, jobStopwatch, jobActivity, ct);
        }
    }
    

    private static void RecordJobSuccess(TStorageRecord storedJob, Stopwatch jobStopwatch, Activity? jobActivity)
    {
        jobStopwatch.Stop();
        JobExecutionDuration.Record(jobStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("job.type", storedJob.JobType),
            new KeyValuePair<string, object?>("job.method", storedJob.MethodName),
            new KeyValuePair<string, object?>("job.status", "success"));
        JobsProcessedCounter.Add(1,
            new KeyValuePair<string, object?>("job.type", storedJob.JobType),
            new KeyValuePair<string, object?>("job.method", storedJob.MethodName));

        jobActivity?.SetStatus(ActivityStatusCode.Ok);
    }

    private async Task RecordJobFailureAsync(
        TStorageRecord storedJob,
        IJobStorageProvider<TStorageRecord> storage,
        Exception ex,
        Stopwatch jobStopwatch,
        Activity? jobActivity,
        CancellationToken ct)
    {
        jobStopwatch.Stop();
        JobExecutionDuration.Record(jobStopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("job.type", storedJob.JobType),
            new KeyValuePair<string, object?>("job.method", storedJob.MethodName),
            new KeyValuePair<string, object?>("job.status", "failed"));
        JobsFailedCounter.Add(1,
            new KeyValuePair<string, object?>("job.type", storedJob.JobType),
            new KeyValuePair<string, object?>("job.method", storedJob.MethodName),
            new KeyValuePair<string, object?>("exception.type", ex.GetType().Name));

        jobActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        jobActivity?.AddException(ex);

        _logger.LogError(ex, "Job execution failed: {TrackingId}", storedJob.TrackingId);
        await storage.OnHandlerExecutionFailureAsync(storedJob, ex, ct);
    }

}
