---
title: Storage provider implementation
sidebar_position: 3
description: Implement the five storage operations used by GUSTO.
---

The storage provider connects the runner to your database. It can use any storage technology and can include additional methods for application-specific behavior.

## Provider methods

| Method | Purpose |
| --- | --- |
| `StoreJobAsync` | Store a new record |
| `GetBatchAsync` | Return the next eligible records |
| `MarkJobAsCompleteAsync` | Record successful completion |
| `CancelJobAsync` | Cancel or remove a record |
| `OnHandlerExecutionFailureAsync` | Apply the application's failure policy |

The interface does not prescribe a schema. Add indexes and application fields that suit the queries and policies implemented by your provider.

For a complete implementation with no database-specific code, see the [bare-bones in-memory storage provider](./first-job.md#3-implement-the-five-operations). It implements all five methods in one class and is a useful starting point for understanding the contract.

## Provider call flow

The queue calls the provider when a job is created. The runner then uses the provider to fetch the job and record its result.

```text
Application
    │
    └─ EnqueueAsync(...)
           └─ StoreJobAsync(record)

Runner
    │
    └─ GetBatchAsync(search, cancellationToken)
           │
           └─ for each returned record
                  ├─ execute job successfully
                  │      └─ MarkJobAsCompleteAsync(record)
                  │
                  └─ job throws or times out
                         └─ OnHandlerExecutionFailureAsync(record, exception)
```

`CancelJobAsync` is called by application code when it wants to cancel a job by tracking ID. It is not part of the normal runner loop.

The provider controls what each callback means.

### Completing a job

The runner calls `MarkJobAsCompleteAsync` after the job method returns successfully. Your implementation must update and persist the record. GUSTO does not set `IsComplete` itself.

```csharp
public async Task MarkJobAsCompleteAsync(
    JobRecord record,
    CancellationToken cancellationToken)
{
    var job = await db.JobRecords.SingleAsync(
        item => item.TrackingId == record.TrackingId,
        cancellationToken);

    job.IsComplete = true;
    await db.SaveChangesAsync(cancellationToken);
}
```

### Handling a failed job

The runner calls `OnHandlerExecutionFailureAsync` when the job throws or reaches its execution timeout. **This is where the provider implements retries and terminal failure behavior.**

For a retry, keep `IsComplete` false and move `ExecuteAfter` into the future:

```csharp
public async Task OnHandlerExecutionFailureAsync(
    JobRecord record,
    Exception exception,
    CancellationToken cancellationToken)
{
    var job = await db.JobRecords.SingleAsync(
        item => item.TrackingId == record.TrackingId,
        cancellationToken);

    job.AttemptCount++;

    if (job.AttemptCount < 5)
    {
        job.ExecuteAfter = DateTime.UtcNow.AddMinutes(5);
    }
    else
    {
        job.State = JobState.Failed;
        job.IsComplete = true;
    }

    await db.SaveChangesAsync(cancellationToken);
}
```

`AttemptCount`, `State`, and `JobState.Failed` are application-defined additions to the job record. See [Failure handling](../operate/failures.md) for retry delays, terminal failures, and idempotency guidance.

## Applying the worker predicate

`GetBatchAsync` receives the worker's baseline `Match` expression. It excludes completed, premature, and expired jobs. Apply it together with the supplied limit:

```csharp
public async Task<IEnumerable<JobRecord>> GetBatchAsync(
    JobSearchParams<JobRecord> search,
    CancellationToken cancellationToken)
{
    return await db.JobRecords
        .Where(search.Match)
        .OrderBy(job => job.CreatedOn)
        .Take(search.Limit)
        .ToListAsync(cancellationToken);
}
```

Whether a LINQ provider can translate the expression depends on that provider. Avoid `.Compile()` for remote database queries: it commonly moves filtering into memory or fails translation.

## Multiple runner instances

When several runner instances share a database, `GetBatchAsync` should claim records as it selects them so the same job is not returned to multiple runners.

The implementation depends on the database. Common approaches include:

- selecting rows with database-specific skip-locked semantics;
- updating a claim token and lease expiry in the same transaction that selects work;
- moving records from a ready collection to an in-progress collection atomically.

Add claim or lease fields to the job record when the selected approach requires them. Job handlers should also tolerate another execution if a process stops before completion is recorded.
