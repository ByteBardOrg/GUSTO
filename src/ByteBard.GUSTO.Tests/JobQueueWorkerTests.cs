using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Newtonsoft.Json;
using ByteBard.GUSTO;

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
    public async Task ExecuteAsync_WhenNoJobsAvailable_DelaysAndContinuesLoop()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob>());

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        var serviceProvider = services.BuildServiceProvider();

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

        using var cts = new CancellationTokenSource(50);

        // Act
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.ReceivedWithAnyArgs().GetBatchAsync(default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenValidJobExists_InvokesJobAndMarksComplete()
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

        var services = new ServiceCollection();
        services.AddScoped<IJobStorageProvider<TestJob>>(_ => storage);
        var serviceProvider = services.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

        // Act
        TestableJob.CreateSemaphore();
        var running = worker.StartAsync(CancellationToken.None);
        await TestableJob.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.Received().MarkJobAsCompleteAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenJobThrowsException_LogsAndHandlesFailure()
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

        var services = new ServiceCollection();
        services.AddScoped<IJobStorageProvider<TestJob>>(_ => storage);
        var serviceProvider = services.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

        // Act
        TestableJob.CreateSemaphore();
        var running = worker.StartAsync(CancellationToken.None);
        await TestableJob.semaphoreSlim.WaitAsync();
        await worker.StopAsync(CancellationToken.None);
        // Assert
        await storage.Received().OnHandlerExecutionFailureAsync(job, Arg.Any<Exception>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenStorageThrowsException_LogsAndDelays()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IEnumerable<TestJob>>>(_ => throw new Exception("storage error"));

        var services = new ServiceCollection();
        services.AddScoped<IJobStorageProvider<TestJob>>(_ => storage);
        var serviceProvider = services.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

        using var cts = new CancellationTokenSource(100);

        // Act
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.Received().GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnqueuedViaJobQueue_JobIsExecutedAndMarkedComplete()
    {
        // Arrange
        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        var jobQueue = new JobQueue<TestJob>(storage);

        TestJob capturedJob = null;
        storage
            .When(x => x.StoreJobAsync(Arg.Any<TestJob>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedJob = ci.Arg<TestJob>());

        var services = new ServiceCollection();
        services.AddScoped<IJobStorageProvider<TestJob>>(_ => storage);
        var serviceProvider = services.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        await jobQueue.EnqueueAsync<TestableJob>(svc => svc.ExecuteAsyncSuccess("from real queue"));

        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(_ => capturedJob != null ? new[] { capturedJob } : Array.Empty<TestJob>());

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

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

    public interface IScopedTestService
    {
        string ProcessData(string input);
        bool IsDisposed { get; }
    }
    
    public interface ISingletonTestService
    {
        string ProcessData(string input);
        bool IsDisposed { get; }
    }
    
    public interface ITransientTestService
    {
        string ProcessData(string input);
        bool IsDisposed { get; }
    }

    public class TestService : ISingletonTestService, IScopedTestService, ITransientTestService, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public string ProcessData(string input)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(TestService));
            return $"Processed: {input}";
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    public class JobWithScopedDependency
    {
        private readonly IScopedTestService _scopedService;
        private readonly ISingletonTestService _singletonService;
        private readonly ITransientTestService  _transientService;
        private readonly ITestResultCollector _resultCollector;

        public JobWithScopedDependency(ISingletonTestService singletonService, IScopedTestService scopedService,ITransientTestService transientService, ITestResultCollector resultCollector)
        {
            _singletonService = singletonService;
            _scopedService = scopedService;
            _transientService = transientService;
            _resultCollector = resultCollector;
        }

        public Task ExecuteWithScopedService(string input)
        {
            try
            {
                var result = _scopedService.ProcessData(input);
                _resultCollector.AddResult(result);
                return Task.CompletedTask;
            }
            finally
            {
                _resultCollector.SignalCompletion();
            }
        }
    }

    public interface ITestResultCollector
    {
        void AddResult(string result);
        void SignalCompletion();
        Task WaitForCompletionAsync(int expectedCount, TimeSpan timeout);
        List<string> GetResults();
    }

    public class TestResultCollector : ITestResultCollector
    {
        private readonly List<string> _results = new();
        private readonly SemaphoreSlim _semaphore = new(0, 10);

        public void AddResult(string result)
        {
            lock (_results)
            {
                _results.Add(result);
            }
        }

        public void SignalCompletion()
        {
            _semaphore.Release();
        }

        public async Task WaitForCompletionAsync(int expectedCount, TimeSpan timeout)
        {
            for (int i = 0; i < expectedCount; i++)
            {
                await _semaphore.WaitAsync(timeout);
            }
        }

        public List<string> GetResults()
        {
            lock (_results)
            {
                return new List<string>(_results);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenJobRequiresScopedService_CreatesAndUsesServiceFromScope()
    {
        // Arrange
        var resultCollector = new TestResultCollector();

        var job = new TestJob
        {
            TrackingId = Guid.NewGuid(),
            JobType = typeof(JobWithScopedDependency).AssemblyQualifiedName,
            MethodName = nameof(JobWithScopedDependency.ExecuteWithScopedService),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "test-data" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob> { job }, new List<TestJob>());

        var services = new ServiceCollection();
        services.AddScoped<IScopedTestService, TestService>();
        services.AddSingleton<ISingletonTestService, TestService>();
        services.AddTransient<ITransientTestService, TestService>();
        services.AddScoped<IJobStorageProvider<TestJob>>(_ => storage);
        services.AddSingleton<ITestResultCollector>(resultCollector);
        var serviceProvider = services.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(GetTestConfig());

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

        // Act
        var running = worker.StartAsync(CancellationToken.None);
        await resultCollector.WaitForCompletionAsync(1, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.Received().MarkJobAsCompleteAsync(job, Arg.Any<CancellationToken>());
        var results = resultCollector.GetResults();
        Assert.Single(results);
        Assert.Equal("Processed: test-data", results[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultipleJobsWithScopedServices_EachJobGetsOwnServiceInstance()
    {
        // Arrange
        var resultCollector = new TestResultCollector();

        var job1 = new TestJob
        {
            TrackingId = Guid.NewGuid(),
            JobType = typeof(JobWithScopedDependency).AssemblyQualifiedName,
            MethodName = nameof(JobWithScopedDependency.ExecuteWithScopedService),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "job1" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var job2 = new TestJob
        {
            TrackingId = Guid.NewGuid(),
            JobType = typeof(JobWithScopedDependency).AssemblyQualifiedName,
            MethodName = nameof(JobWithScopedDependency.ExecuteWithScopedService),
            ArgumentsJson = JsonConvert.SerializeObject(new object[] { "job2" }),
            ExecuteAfter = DateTime.UtcNow,
            IsComplete = false
        };

        var storage = Substitute.For<IJobStorageProvider<TestJob>>();
        storage.GetBatchAsync(Arg.Any<JobSearchParams<TestJob>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TestJob> { job1, job2 }, new List<TestJob>());

        var services = new ServiceCollection();
        services.AddScoped<IScopedTestService, TestService>();
        services.AddSingleton<ISingletonTestService, TestService>();
        services.AddTransient<ITransientTestService, TestService>();
        services.AddScoped<IJobStorageProvider<TestJob>>(_ => storage);
        services.AddSingleton<ITestResultCollector>(resultCollector);
        var serviceProvider = services.BuildServiceProvider();

        var logger = Substitute.For<ILogger<JobQueueWorker<TestJob>>>();
        var config = Options.Create(new GustoConfig { Concurrency = 2, PollInterval = TimeSpan.FromMilliseconds(10), BatchSize = 2 });

        var worker = new JobQueueWorker<TestJob>(serviceProvider, config, logger);

        // Act
        var running = worker.StartAsync(CancellationToken.None);
        await resultCollector.WaitForCompletionAsync(2, TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        // Assert
        await storage.Received().MarkJobAsCompleteAsync(job1, Arg.Any<CancellationToken>());
        await storage.Received().MarkJobAsCompleteAsync(job2, Arg.Any<CancellationToken>());
        var results = resultCollector.GetResults();
        Assert.Equal(2, results.Count);
        Assert.Contains("Processed: job1", results);
        Assert.Contains("Processed: job2", results);
    }

    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    [InlineData(ServiceLifetime.Singleton)]
    public async Task AddGusto_WithDifferentLifetimes_RegistersAndExecutesJobsCorrectly(ServiceLifetime lifetime)
    {
        // Arrange
        var resultCollector = new TestResultCollector();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Gusto:Concurrency"] = "1",
                ["Gusto:PollInterval"] = "00:00:00.010",
                ["Gusto:BatchSize"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<IScopedTestService, TestService>();
        services.AddSingleton<ISingletonTestService, TestService>();
        services.AddTransient<ITransientTestService, TestService>();
        services.AddSingleton<ITestResultCollector>(resultCollector);
        services.AddGusto<TestJob, InMemoryJobStorage>(configuration, lifetime);

        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();
        var jobQueue = serviceProvider.GetRequiredService<JobQueue<TestJob>>();

        // Act
        await jobQueue.EnqueueAsync<JobWithScopedDependency>(j => j.ExecuteWithScopedService("test-lifetime"));

        // Start the hosted service manually
        var hostedService = serviceProvider.GetRequiredService<IHostedService>();
        await hostedService.StartAsync(CancellationToken.None);

        await resultCollector.WaitForCompletionAsync(1, TimeSpan.FromSeconds(5));
        await hostedService.StopAsync(CancellationToken.None);

        // Assert
        var results = resultCollector.GetResults();
        Assert.Single(results);
        Assert.Equal("Processed: test-lifetime", results[0]);
    }

    public class InMemoryJobStorage : IJobStorageProvider<TestJob>
    {
        private static readonly List<TestJob> _jobs = new();

        public Task<IEnumerable<TestJob>> GetBatchAsync(JobSearchParams<TestJob> searchParams, CancellationToken cancellationToken = default)
        {
            lock (_jobs)
            {
                var matching = _jobs.Where(searchParams.Match.Compile())
                    .Take(searchParams.Limit)
                    .ToList();
                return Task.FromResult<IEnumerable<TestJob>>(matching);
            }
        }

        public Task StoreJobAsync(TestJob job, CancellationToken cancellationToken = default)
        {
            lock (_jobs)
            {
                _jobs.Add(job);
            }
            return Task.CompletedTask;
        }

        public Task MarkJobAsCompleteAsync(TestJob job, CancellationToken cancellationToken = default)
        {
            lock (_jobs)
            {
                var existing = _jobs.FirstOrDefault(j => j.TrackingId == job.TrackingId);
                if (existing != null)
                {
                    existing.IsComplete = true;
                }
            }
            return Task.CompletedTask;
        }

        public Task CancelJobAsync(Guid trackingId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task OnHandlerExecutionFailureAsync(TestJob job, Exception exception, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
