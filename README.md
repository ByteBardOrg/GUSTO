# GUSTO
Generic Utility for Scheduling & Task Orchestration.

A lightweight background job processing library for .NET. GUSTO provides a simple way to queue and execute background tasks with just a background service and two interfaces to implement.

## Features

- **Minimal Setup**: Implement two interfaces and you're ready
- **Storage Agnostic**: Use any database (SQL Server, MongoDB, Redis, etc.)
- **Flexible Service Lifetimes**: Configure services as Scoped, Transient, or Singleton
- **Concurrent Processing**: Configurable parallel job execution
- **Job Scheduling**: Schedule jobs for future execution
- **Failure Handling**: Built-in retry logic with customizable strategies
- **OpenTelemetry Support**: Built-in metrics and distributed tracing
- **Easy Testing**: Test hooks for integration testing without polling

## Quick Start

### 1. Install the Package

```bash
dotnet add package ByteBard.GUSTO
```

### 2. Implement the Storage Record

```csharp
public class JobRecord : IJobStorageRecord
{
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; }
    public string MethodName { get; set; }
    public string ArgumentsJson { get; set; }
}
```

### 3. Implement the Storage Provider

```csharp
public class InMemoryJobStorageProvider : IJobStorageProvider<JobRecord>
{
    private readonly List<JobRecord> _jobs = new();
    private readonly object _lock = new();

    public Task StoreJobAsync(JobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.Add(jobStorageRecord);
        }
        return Task.CompletedTask;
    }

    public Task<IEnumerable<JobRecord>> GetBatchAsync(JobSearchParams<JobRecord> parameters, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var results = _jobs.Where(parameters.Match.Compile()).Take(parameters.Limit);
            return Task.FromResult(results);
        }
    }

    public Task MarkJobAsCompleteAsync(JobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.TrackingId == jobStorageRecord.TrackingId);
            if (job != null)
            {
                job.IsComplete = true;
            }
        }
        return Task.CompletedTask;
    }

    public Task CancelJobAsync(Guid trackingId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(j => j.TrackingId == trackingId);
        }
        return Task.CompletedTask;
    }

    public Task OnHandlerExecutionFailureAsync(JobRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken)
    {
        // Simple retry: reschedule for 5 minutes later
        jobStorageRecord.ExecuteAfter = DateTime.UtcNow.AddMinutes(5);
        return Task.CompletedTask;
    }
}
```

### 4. Configure Your Application

```csharp
// Program.cs
services.AddGusto<JobRecord, InMemoryJobStorageProvider>(
    configuration,
    lifetime: ServiceLifetime.Scoped  // Default: Scoped. Options: Scoped, Transient, Singleton
);
```

```json
// appsettings.json
{
  "Gusto": {
    "BatchSize": 50,
    "Concurrency": 8,
    "PollInterval": "00:00:10"
  }
}
```

### 5. Create and Queue Jobs

```csharp
public class EmailService
{
    private readonly IEmailProvider _emailProvider;
    
    public EmailService(IEmailProvider emailProvider)
    {
        _emailProvider = emailProvider;
    }
    
    public async Task SendWelcomeEmailAsync(string email, string userName)
    {
        await _emailProvider.SendAsync(email, "Welcome!", $"Hello {userName}!");
    }
}

// Queue jobs
public class UserController : ControllerBase
{
    private readonly JobQueue<JobRecord> _jobQueue;
    
    public UserController(JobQueue<JobRecord> jobQueue)
    {
        _jobQueue = jobQueue;
    }
    
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        // Queue immediate job
        await _jobQueue.EnqueueAsync<EmailService>(
            service => service.SendWelcomeEmailAsync(request.Email, request.UserName));
        
        // Queue scheduled job with expiration
        var trackingId = await _jobQueue.EnqueueAsync<EmailService>(
            service => service.SendWelcomeEmailAsync(request.Email, request.UserName),
            executeAfter: DateTime.UtcNow.AddHours(1),
            expireOn: DateTime.UtcNow.AddDays(1));
        
        return Ok(new { TrackingId = trackingId });
    }
}
```

## Configuration

- **BatchSize**: Jobs to process per batch (default: 10)
- **Concurrency**: Max parallel jobs (default: Environment.ProcessorCount)
- **PollInterval**: Polling frequency (default: 10 seconds)

## Advanced Patterns

### MongoDB Storage Provider

```csharp
public class MongoJobRecord : IJobStorageRecord
{
    public string Id { get; set; }
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; }
    public string MethodName { get; set; }
    public string ArgumentsJson { get; set; }
    public int? RetryCount { get; set; }
}

public class MongoDbJobStorageProvider : IJobStorageProvider<MongoJobRecord>
{
    private readonly IMongoCollection<MongoJobRecord> _collection;
    
    public MongoDbJobStorageProvider(IMongoDatabase database)
    {
        _collection = database.GetCollection<MongoJobRecord>("jobs");
    }

    public async Task StoreJobAsync(MongoJobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        await _collection.InsertOneAsync(jobStorageRecord, cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<MongoJobRecord>> GetBatchAsync(JobSearchParams<MongoJobRecord> parameters, CancellationToken cancellationToken)
    {
        var filter = Builders<MongoJobRecord>.Filter.Where(parameters.Match);
        return await _collection.Find(filter).Limit(parameters.Limit).ToListAsync(cancellationToken);
    }

    public async Task MarkJobAsCompleteAsync(MongoJobRecord jobStorageRecord, CancellationToken cancellationToken)
    {
        var filter = Builders<MongoJobRecord>.Filter.Eq(x => x.TrackingId, jobStorageRecord.TrackingId);
        var update = Builders<MongoJobRecord>.Update.Set(x => x.IsComplete, true);
        await _collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
    }

    public async Task CancelJobAsync(Guid trackingId, CancellationToken cancellationToken)
    {
        var filter = Builders<MongoJobRecord>.Filter.Eq(x => x.TrackingId, trackingId);
        await _collection.DeleteOneAsync(filter, cancellationToken);
    }

    public async Task OnHandlerExecutionFailureAsync(MongoJobRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken)
    {
        // Simple retry: reschedule for 5 minutes later
        var filter = Builders<MongoJobRecord>.Filter.Eq(x => x.TrackingId, jobStorageRecord.TrackingId);
        var update = Builders<MongoJobRecord>.Update.Set(x => x.ExecuteAfter, DateTime.UtcNow.AddMinutes(5));
        await _collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
    }
}
```

### Exponential Backoff Retry

```csharp
public async Task OnHandlerExecutionFailureAsync(MongoJobRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken)
{
    // Track retry attempts
    jobStorageRecord.RetryCount = (jobStorageRecord.RetryCount ?? 0) + 1;
    
    if (jobStorageRecord.RetryCount > 5)
    {
        // Max retries exceeded - mark as failed
        await MarkJobAsCompleteAsync(jobStorageRecord, cancellationToken);
        return;
    }
    
    // Exponential backoff: 2^retryCount minutes
    var delayMinutes = Math.Pow(2, jobStorageRecord.RetryCount.Value);
    jobStorageRecord.ExecuteAfter = DateTime.UtcNow.AddMinutes(delayMinutes);
    
    // Update the record with new retry info
    var filter = Builders<MongoJobRecord>.Filter.Eq(x => x.TrackingId, jobStorageRecord.TrackingId);
    var update = Builders<MongoJobRecord>.Update
        .Set(x => x.ExecuteAfter, jobStorageRecord.ExecuteAfter)
        .Set(x => x.RetryCount, jobStorageRecord.RetryCount);
    
    await _collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
}
```

### Dead Letter Queue

```csharp
public async Task OnHandlerExecutionFailureAsync(MongoJobRecord jobStorageRecord, Exception exception, CancellationToken cancellationToken)
{
    jobStorageRecord.RetryCount = (jobStorageRecord.RetryCount ?? 0) + 1;

    if (jobStorageRecord.RetryCount > 3)
    {
        // Move to dead letter collection for manual inspection
        await _deadLetterCollection.InsertOneAsync(new DeadLetterJob
        {
            OriginalJob = jobStorageRecord,
            FailureReason = exception.Message,
            FailedAt = DateTime.UtcNow
        }, cancellationToken: cancellationToken);

        await MarkJobAsCompleteAsync(jobStorageRecord, cancellationToken);
        return;
    }

    // Continue with retry logic...
}
```

### Service Lifetime Configuration

GUSTO supports Scoped, Transient, and Singleton service lifetimes for `JobQueue` and `IJobStorageProvider`:

```csharp
// Scoped (default) - New instance per batch
services.AddGusto<JobRecord, InMemoryJobStorageProvider>(
    configuration,
    ServiceLifetime.Scoped);

// Transient - New instance for every resolution
services.AddGusto<JobRecord, InMemoryJobStorageProvider>(
    configuration,
    ServiceLifetime.Transient);

// Singleton - Single instance for application lifetime
services.AddGusto<JobRecord, InMemoryJobStorageProvider>(
    configuration,
    ServiceLifetime.Singleton);
```

**Use Cases:**
- **Scoped**: Best for database contexts (EF Core DbContext, MongoDB scoped collections)
- **Singleton**: Best for thread-safe in-memory implementations or stateless providers
- **Transient**: Rarely needed, but available for special cases

## OpenTelemetry Integration

GUSTO includes built-in OpenTelemetry support for metrics and distributed tracing.

### Available Metrics

| Metric | Type | Description | Tags |
|--------|------|-------------|------|
| `gusto.jobs.processed` | Counter | Total successful jobs | `job.type`, `job.method` |
| `gusto.jobs.failed` | Counter | Total failed jobs | `job.type`, `job.method`, `exception.type` |
| `gusto.job.duration` | Histogram | Job execution time (ms) | `job.type`, `job.method`, `job.status` |
| `gusto.batch.duration` | Histogram | Batch processing time (ms) | - |
| `gusto.batch.size` | Histogram | Jobs per batch | - |

### Available Traces

- **ProcessBatch** - Span for entire batch with `batch.size` tag
- **ExecuteJob** - Span for individual jobs with `job.tracking_id`, `job.type`, `job.method` tags

### Configuration

Add GUSTO telemetry to your OpenTelemetry configuration:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(GustoTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(GustoTelemetry.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());
```

Export to Prometheus, Grafana, Jaeger, or any OpenTelemetry-compatible backend.

## Testing

GUSTO provides test barriers for easy integration testing without polling or delays.

### Integration Test Example

```csharp
[Fact]
public async Task JobQueue_ProcessesJobSuccessfully()
{
    // Arrange
    var services = new ServiceCollection();
    services.AddGusto<JobRecord, InMemoryJobStorageProvider>(configuration);
    services.AddScoped<MyService>();

    var serviceProvider = services.BuildServiceProvider();
    var jobQueue = serviceProvider.GetRequiredService<JobQueue<JobRecord>>();
    var hostedService = serviceProvider.GetRequiredService<IHostedService>();

    // Set up test barriers
    JobQueueWorker<JobRecord>.BatchStartBarrier = new TaskCompletionSource();
    JobQueueWorker<JobRecord>.BatchCompletedBarrier = new TaskCompletionSource();

    // Act
    await jobQueue.EnqueueAsync<MyService>(s => s.DoWork("test"));

    await hostedService.StartAsync(CancellationToken.None);

    // Let worker process the batch
    JobQueueWorker<JobRecord>.BatchStartBarrier.SetResult();

    // Wait for batch to complete
    await JobQueueWorker<JobRecord>.BatchCompletedBarrier.Task;

    await hostedService.StopAsync(CancellationToken.None);

    // Assert
    Assert.True(MyService.WorkCompleted);
}
```

### Test Barriers

- **`BatchStartBarrier`** - Pauses worker at start of batch cycle until signaled
- **`BatchCompletedBarrier`** - Signals when batch processing completes

This eliminates flaky tests from polling and arbitrary delays.
