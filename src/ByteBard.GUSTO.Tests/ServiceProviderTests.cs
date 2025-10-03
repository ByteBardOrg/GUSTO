using System.Collections.Concurrent;
using ByteBard.GUSTO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class ServiceProviderScopeTests
{
    [Fact]
    public async Task StorageProvider_UsedConcurrently_ThrowsDbContextConcurrencyError()
    {
        // This test demonstrates a bug with scoped services (particular dbcontext usage): when multiple jobs run in parallel,
        // they share the same storage provider instance (and its scoped DbContext),
        // causing EF Core to throw a concurrency exception

        FakeDbContext.SeenHashes.Clear();
        FakeDbContext.DetectedConcurrentAccess = false;
        FakeJobService.Completed.Clear();

        var services = new ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Gusto:BatchSize"] = "5",
            ["Gusto:Concurrency"] = "5",
            ["Gusto:PollInterval"] = "00:00:01"
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
        services.AddLogging();
        services.AddGusto<FakeJobRecord, InMemoryJobStorageProvider>(configuration);

        services.AddScoped<IFakeDbContext, FakeDbContext>();
        services.AddScoped<FakeJobService>();

        var sp = services.BuildServiceProvider();
        var jobQueue = sp.GetRequiredService<JobQueue<FakeJobRecord>>();
        var worker = sp.GetRequiredService<IHostedService>();

        // Arrange barriers
        JobQueueWorker<FakeJobRecord>.BatchStartBarrier = new TaskCompletionSource();
        JobQueueWorker<FakeJobRecord>.BatchCompletedBarrier = new TaskCompletionSource();
        
        for (int i = 0; i < 5; i++)
        {
            await jobQueue.EnqueueAsync<FakeJobService>(
                svc => svc.RunAsync($"job-{i}"));
        }

        // Act
        await worker.StartAsync(CancellationToken.None);
        JobQueueWorker<FakeJobRecord>.BatchStartBarrier.SetResult();
        await JobQueueWorker<FakeJobRecord>.BatchCompletedBarrier.Task;
        await worker.StopAsync(CancellationToken.None);
        
        Assert.False(FakeDbContext.DetectedConcurrentAccess,
            "Concurrent DbContext access was detected! " +
            "Multiple parallel jobs are sharing the same storage provider instance " +
            "(with the same scoped DbContext). Each job should get its own scope.");
    }

}
public interface IFakeDbContext
{
    Task DoWorkAsync(string jobId);
}

public class FakeDbContext : IFakeDbContext
{
    public static ConcurrentBag<int> SeenHashes = new();
    public static volatile bool DetectedConcurrentAccess = false;

    private int _inUse;

    public async Task DoWorkAsync(string jobId)
    {
        SeenHashes.Add(GetHashCode());
        if (Interlocked.Exchange(ref _inUse, 1) == 1)
        {
            DetectedConcurrentAccess = true;
            throw new InvalidOperationException(
                $"A second operation was started on this context instance before a previous operation completed. " +
                $"DbContext {GetHashCode()} already in use! This simulates EF Core's concurrency detection.");
        }

        try { await Task.Delay(50); }
        finally { Interlocked.Exchange(ref _inUse, 0); }
    }
}


public class FakeJobService
{
    private readonly IFakeDbContext _db;
    public static ConcurrentBag<string> Completed = new();

    public FakeJobService(IFakeDbContext db) => _db = db;

    public async Task RunAsync(string id)
    {
        await _db.DoWorkAsync(id);
        Completed.Add(id);
    }
}

public class InMemoryJobStorageProvider : IJobStorageProvider<FakeJobRecord>
{
    private static readonly List<FakeJobRecord> _jobs = new();
    private readonly object _lock = new();
    private readonly IFakeDbContext _dbContext;

    public InMemoryJobStorageProvider(IFakeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task StoreJobAsync(FakeJobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.Add(jobStorageRecord);
        }
        return Task.CompletedTask;
    }

    public Task<IEnumerable<FakeJobRecord>> GetBatchAsync(JobSearchParams<FakeJobRecord> parameters, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var results = _jobs.Where(j => j.Status == JobStatus.Ready).Where(parameters.Match.Compile()).Take(parameters.Limit);
            return Task.FromResult(results);
        }
    }

    public async Task MarkJobAsCompleteAsync(FakeJobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        // Simulate async database operation like EF Core would do
        await _dbContext.DoWorkAsync($"MarkComplete-{jobStorageRecord.TrackingId}");

        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.TrackingId == jobStorageRecord.TrackingId);
            if (job != null)
            {
                job.IsComplete = true;

                ProcessContinuations(job.TrackingId, successful: true);
            }
        }
    }

    public Task CancelJobAsync(Guid trackingId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(j => j.TrackingId == trackingId);
        }
        return Task.CompletedTask;
    }

    public async Task OnHandlerExecutionFailureAsync(FakeJobRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken)
    {
        // Simulate async database operation like EF Core would do
        await _dbContext.DoWorkAsync($"HandleFailure-{jobStorageRecord.TrackingId}");

        lock (_lock)
        {
            jobStorageRecord.ExecuteAfter = DateTime.UtcNow.AddMinutes(5);
        }
    }

    private void ProcessContinuations(Guid completedJobId, bool successful)
    {
        var continuations = _jobs.Where(j =>
            j.Status == JobStatus.WaitingForParent &&
            j.ParentJobId == completedJobId).ToList();

        foreach (var continuation in continuations)
        {
            if (successful)
            {
                continuation.Status = JobStatus.Ready;
                continuation.ExecuteAfter = DateTime.UtcNow;

                // don't forget to store the change if moving to persisted storage.
            }
        }
    }
}

public class FakeJobRecord : IJobStorageRecord
{
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; }
    public string MethodName { get; set; }
    public string ArgumentsJson { get; set; }
    public JobStatus Status { get; set; }
    public Guid ParentJobId { get; set; }
}

public enum JobStatus
{
    Ready,
    WaitingForParent,
}
