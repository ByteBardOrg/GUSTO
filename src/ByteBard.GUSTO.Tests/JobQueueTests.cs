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

    private class TestJob
    {
        public virtual Task DoSomethingAsync(string input)
        {
            return Task.CompletedTask;
        }
    }
    
    [Fact]
    public async Task EnqueueAsync_ValidMethodCall_StoresSerializedJob()
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
    public async Task EnqueueAsyncGeneric_ValidMethodCall_StoresSerializedJob()
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
    public async Task EnqueueAsync_WithExecuteAfter_UsesProvidedTime()
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
    public async Task EnqueueAsync_WithoutExecuteAfter_SetsToUtcNow()
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
}
