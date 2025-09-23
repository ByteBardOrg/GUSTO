namespace ByteBard.GUSTO;

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

    private readonly IServiceProvider _serviceProvider;
    private readonly IJobStorageProvider<TStorageRecord> _storage;
    private readonly ILogger<JobQueueWorker<TStorageRecord>> _logger;
    private readonly GustoConfig _config;
    
    public JobQueueWorker(
        IServiceProvider serviceProvider,
        IJobStorageProvider<TStorageRecord> storage,
        IOptions<GustoConfig> config,
        ILogger<JobQueueWorker<TStorageRecord>> logger)
    {
        _serviceProvider = serviceProvider;
        _storage = storage;
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
                var jobs = await _storage.GetBatchAsync(
                    new JobSearchParams<TStorageRecord>
                {
                    Match = job =>
                        !job.IsComplete &&
                        job.ExecuteAfter <= DateTime.UtcNow &&
                        (job.ExpireOn == null || job.ExpireOn > DateTime.UtcNow),
                    Limit = _config.BatchSize,
                    CancellationToken = stoppingToken
                }, stoppingToken);

                var jobStorageRecords = jobs.ToList();
                if (!jobStorageRecords.Any())
                {
                    await Task.Delay(_config.PollInterval, stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(jobStorageRecords, parallelOptions, async (storedJob, ct) =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    try
                    {
                        var jobType = Type.GetType(storedJob.JobType);
                        var arguments = JsonConvert.DeserializeObject<object[]>(storedJob.ArgumentsJson, _settings);
                        var jobInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, jobType);
                        var method = jobType.GetMethod(storedJob.MethodName);
                        await (Task)method.Invoke(jobInstance, arguments);

                        await _storage.MarkJobAsCompleteAsync(storedJob, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Job execution failed: {TrackingId}", storedJob.TrackingId);
                        await _storage.OnHandlerExecutionFailureAsync(storedJob, ex, ct);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure in JobQueueWorker.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("JobQueueWorker stopped.");
    }
}
