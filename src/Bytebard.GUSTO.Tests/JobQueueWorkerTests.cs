using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Newtonsoft.Json;
using Bytebard.GUSTO;

public class JobQueueWorkerTests
{
    public class TestJob : IJobStorageRecord
    {
        public Guid TrackingId { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime? ExecuteAfter { get; set; }
        public DateTime? ExpireOn { get; set; }
        public string JobType { get; set; }
        public string MethodName { get; set; }
        public string ArgumentsJson { get; set; }
        public bool IsComplete { get; set; }
    }

    public class DummyService
    {
        public SemaphoreSlim semaphoreSlim = new SemaphoreSlim(0, 1);
        public virtual Task ExecuteAsyncSuccess(string input)
        {
            try
            {
                return Task.CompletedTask;
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }
        
        public virtual Task ExecuteAsyncFail(string input)
        {
            try
            {
                throw new InvalidOperationException("boom");
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }
    }

    private JobQueueConfig GetTestConfig() => new JobQueueConfig
    {
        Concurrency = 1,
        PollInterval = TimeSpan.FromMilliseconds(10),
        BatchSize = 1
    };

    [Fact]
    public async Task ExecuteAsync_NoJobs_DelaysAndContinuesLoop()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetNextBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob>());

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());
        var services = Substitute.For<IServiceProvider>();

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);

        using var cts = new CancellationTokenSource(50);

        // Act
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.ReceivedWithAnyArgs().GetNextBatchAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_ValidJob_InvokesJobAndMarksComplete()
    {
        // Arrange
        var dummy = new DummyService();

        var job = new TestJob
        {
            TrackingId = Guid.NewGuid(),
            JobType = typeof(DummyService).AssemblyQualifiedName,
            MethodName = nameof(DummyService.ExecuteAsyncSuccess),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "test" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetNextBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob> { job });

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<DummyService>(dummy);
        var services = serviceCollection.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);

        using var cts = new CancellationTokenSource(50);

        // Act
        await worker.StartAsync(cts.Token);
        await dummy.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.Received().MarkJobAsCompleteAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_JobThrowsException_LogsAndHandlesFailure()
    {
        // Arrange
        var job = new TestJob
        {
            TrackingId = Guid.NewGuid(),
            JobType = typeof(DummyService).AssemblyQualifiedName,
            MethodName = nameof(DummyService.ExecuteAsyncFail),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "fail" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetNextBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob> { job });

        var failingService = new DummyService();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<DummyService>(failingService);
        var services = serviceCollection.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);
        
        // Act
        var running = worker.StartAsync(CancellationToken.None);
        await failingService.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);
        // Assert
        await storage.Received().OnHandlerExecutionFailureAsync(job, Arg.Any<Exception>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StorageThrowsException_LogsAndDelays()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetNextBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IEnumerable<TestJob>>>(_ => throw new Exception("storage error"));

        var services = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);

        using var cts = new CancellationTokenSource(100);

        // Act
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.Received().GetNextBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EnqueuedViaJobQueue_JobIsExecutedAndMarkedComplete()
    {
        // Arrange
        var dummy = new DummyService();
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        var jobQueue = new JobQueue<TestJob>(storage);

        TestJob capturedJob = null;
        storage
            .When(x => x.StoreJobAsync(Arg.Any<TestJob>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedJob = ci.Arg<TestJob>());

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<DummyService>(dummy);
        var services = serviceCollection.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        await jobQueue.EnqueueAsync<DummyService>(svc => svc.ExecuteAsyncSuccess("from real queue"));

        storage.GetNextBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(_ => capturedJob != null ? new[] { capturedJob } : Array.Empty<TestJob>());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);

        using var cts = new CancellationTokenSource(150);

        // Act
        var running = worker.StartAsync(cts.Token);
        await dummy.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(capturedJob);
        await storage.Received().MarkJobAsCompleteAsync(capturedJob, Arg.Any<CancellationToken>());
        Assert.Equal("from real queue", JsonConvert.DeserializeObject<string[]>(capturedJob.ArgumentsJson)[0]);
    }
}
