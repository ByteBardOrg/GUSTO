using System.Linq.Expressions;
using Newtonsoft.Json;
using NSubstitute;
using ByteBard.GUSTO;

public class JobQueueTests
{
    public class TestJobStorageRecord : IJobStorageRecord
    {
        public Guid TrackingId { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime? ExpireOn { get; set; }
        public DateTime? ExecuteAfter { get; set; }
        public string JobType { get; set; }
        public string MethodName { get; set; }
        public string ArgumentsJson { get; set; }
        public bool IsComplete { get; set; }
    }

    private interface ITestJob
    {
        Task DoSomethingAsync(string input);
    }
    
    private class TestJob : ITestJob
    {
        public virtual Task DoSomethingAsync(string input)
        {
            return Task.CompletedTask;
        }
    }
    
    [Fact]
    public async Task EnqueueAsync_WhenValidInterfaceJobTypeProvided_StoresSerializedJobWithRealType()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var cancellationToken = CancellationToken.None;
        ITestJob testJob = new TestJob();
        
        // Act
        var trackingId = await jobQueue.EnqueueAsync(() => testJob.DoSomethingAsync("hello"), null, cancellationToken);

        // Assert
        await storageProvider.Received(1).StoreJobAsync(
            Arg.Is<TestJobStorageRecord>(record =>
                record.TrackingId == trackingId &&
                record.MethodName == "DoSomethingAsync" &&
                record.JobType == typeof(TestJob).AssemblyQualifiedName &&
                JsonConvert.DeserializeObject<string[]>(record.ArgumentsJson)[0] == "hello" &&
                !record.IsComplete),
            cancellationToken);
    }
    
    [Fact]
    public async Task EnqueueAsync_WhenValidMethodCallProvided_StoresSerializedJob()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var cancellationToken = CancellationToken.None;
        var testJob = new TestJob();
        
        // Act
        var trackingId = await jobQueue.EnqueueAsync(() => testJob.DoSomethingAsync("hello"), null, cancellationToken);

        // Assert
        await storageProvider.Received(1).StoreJobAsync(
            Arg.Is<TestJobStorageRecord>(record =>
                record.TrackingId == trackingId &&
                record.MethodName == "DoSomethingAsync" &&
                record.JobType == typeof(TestJob).AssemblyQualifiedName &&
                JsonConvert.DeserializeObject<string[]>(record.ArgumentsJson)[0] == "hello" &&
                !record.IsComplete),
            cancellationToken);
    }


    [Fact]
    public async Task EnqueueAsyncGeneric_WhenValidMethodCallProvided_StoresSerializedJob()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var cancellationToken = CancellationToken.None;

        // Act
        var trackingId = await jobQueue.EnqueueAsync<TestJob>(job => job.DoSomethingAsync("hello"), null, cancellationToken);

        // Assert
        await storageProvider.Received(1).StoreJobAsync(
            Arg.Is<TestJobStorageRecord>(record =>
                record.TrackingId == trackingId &&
                record.MethodName == "DoSomethingAsync" &&
                record.JobType == typeof(TestJob).AssemblyQualifiedName &&
                JsonConvert.DeserializeObject<string[]>(record.ArgumentsJson)[0] == "hello" &&
                !record.IsComplete),
            cancellationToken);
    }

    [Fact]
    public async Task EnqueueAsync_WhenExecuteAfterTimeProvided_UsesProvidedTime()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var executeAfter = DateTime.UtcNow.AddMinutes(10);
        var cancellationToken = CancellationToken.None;

        // Act
        await jobQueue.EnqueueAsync<TestJob>(job => job.DoSomethingAsync("hello"), executeAfter, cancellationToken);

        // Assert
        await storageProvider.Received(1).StoreJobAsync(
            Arg.Is<TestJobStorageRecord>(record =>
                Math.Abs((record.ExecuteAfter.Value - executeAfter).TotalSeconds) < 1),
            cancellationToken);
    }

    [Fact]
    public async Task EnqueueAsync_WhenExecuteAfterNotProvided_SetsToUtcNow()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var beforeCall = DateTime.UtcNow;
        var cancellationToken = CancellationToken.None;

        // Act
        await jobQueue.EnqueueAsync<TestJob>(job => job.DoSomethingAsync("hello"), null, cancellationToken);
        var afterCall = DateTime.UtcNow;

        // Assert
        await storageProvider.Received(1).StoreJobAsync(
            Arg.Is<TestJobStorageRecord>(record =>
                record.ExecuteAfter >= beforeCall && record.ExecuteAfter <= afterCall),
            cancellationToken);
    }

    public class JobWithMultipleParameters
    {
        public Task ProcessAsync(string message, int count, bool isEnabled)
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task EnqueueAsync_WhenMultipleParametersProvided_SerializesAllArguments()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var cancellationToken = CancellationToken.None;

        // Act
        var trackingId = await jobQueue.EnqueueAsync<JobWithMultipleParameters>(
            job => job.ProcessAsync("hello", 42, true),
            null,
            cancellationToken);

        // Assert
        await storageProvider.Received(1).StoreJobAsync(
            Arg.Is<TestJobStorageRecord>(record =>
                record.TrackingId == trackingId &&
                record.MethodName == "ProcessAsync" &&
                record.JobType == typeof(JobWithMultipleParameters).AssemblyQualifiedName &&
                JsonConvert.DeserializeObject<object[]>(record.ArgumentsJson).Length == 3 &&
                JsonConvert.DeserializeObject<object[]>(record.ArgumentsJson)[0].ToString() == "hello" &&
                JsonConvert.DeserializeObject<object[]>(record.ArgumentsJson)[1].ToString() == "42" &&
                JsonConvert.DeserializeObject<object[]>(record.ArgumentsJson)[2].ToString() == "True"),
            cancellationToken);
    }

    [Fact]
    public async Task ConstructRecordFromExpression_WhenCalled_ReturnsValidRecord()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var executeAfter = DateTime.UtcNow.AddMinutes(5);

        // Act
        var record = jobQueue.ConstructRecordFromExpression<TestJob>(
            job => job.DoSomethingAsync("test"),
            executeAfter);

        // Assert
        Assert.NotEqual(Guid.Empty, record.TrackingId);
        Assert.Equal("DoSomethingAsync", record.MethodName);
        Assert.Equal(typeof(TestJob).AssemblyQualifiedName, record.JobType);
        Assert.False(record.IsComplete);
        Assert.Equal(executeAfter, record.ExecuteAfter);
        Assert.True(record.CreatedOn <= DateTime.UtcNow);

        var args = JsonConvert.DeserializeObject<object[]>(record.ArgumentsJson);
        Assert.Single(args);
        Assert.Equal("test", args[0]);
    }

    [Fact]
    public async Task EnqueueAsync_WhenStorageProviderThrowsException_PropagatesException()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        storageProvider.StoreJobAsync(Arg.Any<TestJobStorageRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Storage error"));

        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await jobQueue.EnqueueAsync<TestJob>(job => job.DoSomethingAsync("test"), null, cancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_WhenCancellationRequested_PropagatesCancellationToStorage()
    {
        // Arrange
        var storageProvider = Substitute.For<IJobStorageProvider<TestJobStorageRecord>>();
        storageProvider.StoreJobAsync(Arg.Any<TestJobStorageRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled(new CancellationToken(true)));

        var jobQueue = new JobQueue<TestJobStorageRecord>(storageProvider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await jobQueue.EnqueueAsync<TestJob>(job => job.DoSomethingAsync("test"), null, cts.Token));
    }
}
