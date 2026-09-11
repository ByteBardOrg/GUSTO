---
title: Public API
sidebar_position: 1
description: Public interfaces and methods provided by ByteBard.GUSTO.
---

This page lists the API used to configure, enqueue, and extend GUSTO. The signatures match the current `main` branch.

## Registration

```csharp
public static IServiceCollection AddGusto<TStorageRecord, TStorageProvider>(
    this IServiceCollection services,
    IConfiguration configuration,
    ServiceLifetime lifetime = ServiceLifetime.Scoped)
    where TStorageRecord : class, IJobStorageRecord, new()
    where TStorageProvider : class, IJobStorageProvider<TStorageRecord>
```

Pass the root application configuration. `AddGusto` reads the `Gusto` section internally. It registers:

- `JobQueue<TStorageRecord>` with the selected lifetime;
- `IJobStorageProvider<TStorageRecord>` using `TStorageProvider` and the selected lifetime;
- `JobQueueWorker<TStorageRecord>` as a hosted service.

Do not separately register the same storage provider unless you intend to override the registration.

## `IJobStorageRecord`

```csharp
public interface IJobStorageRecord
{
    Guid TrackingId { get; set; }
    DateTime CreatedOn { get; set; }
    DateTime? ExecuteAfter { get; set; }
    DateTime? ExpireOn { get; set; }
    bool IsComplete { get; set; }
    string JobType { get; set; }
    string MethodName { get; set; }
    string ArgumentsJson { get; set; }
}
```

Applications can add any fields required by their provider, including claim leases, priority, tenant IDs, retry counts, batch IDs, and workflow state.

## `IJobStorageProvider<TStorageRecord>`

```csharp
public interface IJobStorageProvider<TStorageRecord>
    where TStorageRecord : IJobStorageRecord
{
    Task StoreJobAsync(
        TStorageRecord record,
        CancellationToken cancellationToken);

    Task<IEnumerable<TStorageRecord>> GetBatchAsync(
        JobSearchParams<TStorageRecord> parameters,
        CancellationToken cancellationToken);

    Task MarkJobAsCompleteAsync(
        TStorageRecord record,
        CancellationToken cancellationToken);

    Task CancelJobAsync(
        Guid trackingId,
        CancellationToken cancellationToken);

    Task OnHandlerExecutionFailureAsync(
        TStorageRecord record,
        Exception exception,
        CancellationToken cancellationToken);
}
```

The provider is the main extension point. GUSTO does not restrict the storage technology or additional methods exposed by a concrete provider.

## `JobQueue<TStorageRecord>`

```csharp
public Task<Guid> EnqueueAsync<T>(
    Expression<Func<T, Task>> methodCall,
    DateTime? executeAfter = null,
    CancellationToken cancellationToken = default);

public Task<Guid> EnqueueAsync(
    Expression<Func<Task>> methodCall,
    DateTime? executeAfter = null,
    CancellationToken cancellationToken = default);

public Task<Guid> EnqueueAsync<T>(
    EnqueueOptions options,
    Expression<Func<T, Task>> methodCall,
    CancellationToken cancellationToken = default);

public Task<Guid> EnqueueAsync(
    EnqueueOptions options,
    Expression<Func<Task>> methodCall,
    CancellationToken cancellationToken = default);
```

All overloads return the generated tracking ID after `StoreJobAsync` completes. `EnqueueOptions` exposes `DateTime? ExecuteAfter` and `ActivityContext? ParentContext`. Existing overloads forward their schedule to the options path; the options-first shape avoids ambiguity with existing calls that pass `null` for `executeAfter`.

The queue also exposes three `ConstructRecordFromExpression` overloads and its `StorageProvider`. These members allow application extension methods to construct records, set application-specific fields, and store them.

## `JobSearchParams<TStorageRecord>`

The worker passes `Match`, `Limit`, and `CancellationToken` to `GetBatchAsync`. Their setters are internal; providers read and apply them.

## Telemetry constants

`GustoTelemetry.ActivitySourceName` and `GustoTelemetry.MeterName` both contain `ByteBard.GUSTO.JobQueue`. Register both with the application's tracing and metrics pipelines, and use these constants rather than copying the string.
