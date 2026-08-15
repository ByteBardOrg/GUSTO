---
title: First job
sidebar_position: 2
description: Install GUSTO and run one job through an in-memory provider.
---

This first queue uses memory so the GUSTO contract is easy to see. It is suitable for learning and tests, not durable work.

## 1. Install the package

```bash
dotnet add package ByteBard.GUSTO
```

GUSTO currently targets .NET 8 and .NET 9.

## 2. Define the record

The record belongs to your application, but it must implement the fields the worker needs.

```csharp
using ByteBard.GUSTO;

public sealed class JobRecord : IJobStorageRecord
{
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; } = null!;
    public string MethodName { get; set; } = null!;
    public string ArgumentsJson { get; set; } = null!;
}
```

## 3. Implement the five operations

```csharp
public sealed class InMemoryJobStorageProvider
    : IJobStorageProvider<JobRecord>
{
    private readonly List<JobRecord> _jobs = [];
    private readonly object _lock = new();

    public Task StoreJobAsync(
        JobRecord record,
        CancellationToken cancellationToken)
    {
        lock (_lock) _jobs.Add(record);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<JobRecord>> GetBatchAsync(
        JobSearchParams<JobRecord> search,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<JobRecord>>(
                _jobs.Where(search.Match.Compile())
                     .Take(search.Limit)
                     .ToList());
        }
    }

    public Task MarkJobAsCompleteAsync(
        JobRecord record,
        CancellationToken cancellationToken)
    {
        lock (_lock) record.IsComplete = true;
        return Task.CompletedTask;
    }

    public Task CancelJobAsync(
        Guid trackingId,
        CancellationToken cancellationToken)
    {
        lock (_lock) _jobs.RemoveAll(job => job.TrackingId == trackingId);
        return Task.CompletedTask;
    }

    public Task OnHandlerExecutionFailureAsync(
        JobRecord record,
        Exception exception,
        CancellationToken cancellationToken)
    {
        lock (_lock) record.ExecuteAfter = DateTime.UtcNow.AddMinutes(5);
        return Task.CompletedTask;
    }
}
```

:::note About the sample
This provider returns references to mutable records and has no durability or distributed claim. It is intended to demonstrate the interface only.
:::

## 4. Register GUSTO and a job service

```csharp
builder.Services.AddGusto<JobRecord, InMemoryJobStorageProvider>(
    builder.Configuration,
    ServiceLifetime.Singleton);

builder.Services.AddScoped<EmailService>();
```

`AddGusto` reads the `Gusto` configuration section, registers the queue and provider, and adds the hosted worker. This example uses a singleton because the queue and worker must share the same in-memory list. Database providers are normally scoped.

## 5. Enqueue work

```csharp
public sealed class RegistrationService(JobQueue<JobRecord> queue)
{
    public async Task RegisterAsync(Guid userId)
    {
        // Save the user first, then enqueue the follow-up work.
        await queue.EnqueueAsync<EmailService>(
            service => service.SendWelcomeEmailAsync(userId));
    }
}
```

The returned `Guid` is the job's tracking ID. Store it if your application needs cancellation or status lookup.

The queue now runs, but memory concealed the important design work. The next page turns the interface into a production storage checklist.
