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

    public class TestableJob
    {
        public static SemaphoreSlim semaphoreSlim { get; private set; }

        public static void CreateSemaphore()
        {
            semaphoreSlim = new SemaphoreSlim(0, 1);
        }
            
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

    private GustoConfig GetTestConfig() => new GustoConfig
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
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
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
        await storage.ReceivedWithAnyArgs().GetBatchAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_ValidJob_InvokesJobAndMarksComplete()
    {
        // Arrange
        var job = new TestJob
        {
            TrackingId = Guid.NewGuid(),
            JobType = typeof(TestableJob).AssemblyQualifiedName,
            MethodName = nameof(TestableJob.ExecuteAsyncSuccess),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "test" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob> { job });

        var services = new ServiceCollection().BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);
        
        // Act
        TestableJob.CreateSemaphore();
        var running = worker.StartAsync(CancellationToken.None);
        await TestableJob.semaphoreSlim.WaitAsync();
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
            JobType = typeof(TestableJob).AssemblyQualifiedName,
            MethodName = nameof(TestableJob.ExecuteAsyncFail),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "fail" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob> { job });
        
        var services = new ServiceCollection().BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);
        
        // Act
        TestableJob.CreateSemaphore();
        var running = worker.StartAsync(CancellationToken.None);
        await TestableJob.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);
        // Assert
        await storage.Received().OnHandlerExecutionFailureAsync(job, Arg.Any<Exception>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StorageThrowsException_LogsAndDelays()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
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
        await storage.Received().GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EnqueuedViaJobQueue_JobIsExecutedAndMarkedComplete()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        var jobQueue = new JobQueue<TestJob>(storage);

        TestJob capturedJob = null;
        storage
            .When(x => x.StoreJobAsync(Arg.Any<TestJob>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedJob = ci.Arg<TestJob>());

        var services = new ServiceCollection().BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        await jobQueue.EnqueueAsync<TestableJob>(svc => svc.ExecuteAsyncSuccess("from real queue"));

        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(_ => capturedJob != null ? new[] { capturedJob } : Array.Empty<TestJob>());

        var worker = new JobQueueWorker<TestJob>(services, storage, config, logger);

        using var cts = new CancellationTokenSource(150);

        // Act
        TestableJob.CreateSemaphore();
        var running = worker.StartAsync(cts.Token);
        await TestableJob.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(capturedJob);
        await storage.Received().MarkJobAsCompleteAsync(capturedJob, Arg.Any<CancellationToken>());
        Assert.Equal("from real queue", JsonConvert.DeserializeObject<string[]>(capturedJob.ArgumentsJson)[0]);
    }
}
