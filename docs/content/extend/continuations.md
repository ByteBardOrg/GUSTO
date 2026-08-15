---
title: Continuation example
sidebar_position: 3
description: Keep a job in a waiting state until its parent reaches a terminal state.
---

A continuation is a persisted job that is intentionally ineligible until another job finishes. This requires an explicit state beyond the base `IsComplete` flag.

```csharp
public Guid? ParentJobId { get; set; }
public JobState State { get; set; } = JobState.Ready;

public enum JobState
{
    Ready,
    Running,
    WaitingForParent,
    Succeeded,
    Failed
}
```

## Creating a waiting job

```csharp
public static async Task<Guid> ContinueWithAsync(
    this JobQueue<JobRecord> queue,
    Guid parentJobId,
    Expression<Func<Task>> call,
    CancellationToken cancellationToken = default)
{
    var record = queue.ConstructRecordFromExpression(call, executeAfter: null);
    record.ParentJobId = parentJobId;
    record.State = JobState.WaitingForParent;

    await queue.StorageProvider.StoreJobAsync(record, cancellationToken);
    return record.TrackingId;
}
```

Your provider's `GetBatchAsync` must add `State == JobState.Ready` to the baseline worker predicate. Otherwise the waiting record remains eligible because GUSTO does not understand application states.

## Updating continuations after completion

When the provider marks a parent successful, update waiting children to `Ready` in the same transaction:

```csharp
await db.JobRecords
    .Where(job =>
        job.ParentJobId == parent.TrackingId &&
        job.State == JobState.WaitingForParent)
    .ExecuteUpdateAsync(update => update
        .SetProperty(job => job.State, JobState.Ready)
        .SetProperty(job => job.ExecuteAfter, DateTime.UtcNow),
        cancellationToken);
```

Define the failure branch too. A child might be cancelled, moved to failed state, or released despite parent failure. Leaving it waiting forever is usually the least useful accidental policy.

## Batch continuations

To continue after a batch, release the child only when every batch member is terminal and the required success condition holds. Evaluate and release transactionally; two members may finish at the same time.

At this point the provider owns a real state machine. Tests should cover every parent and child transition, including duplicate completion callbacks.
