---
title: Continuation example
sidebar_position: 3
description: Keep a job in a waiting state until its parent reaches a terminal state.
---

A continuation is a persisted job that is intentionally ineligible until another job finishes. This requires an explicit state beyond the base `IsComplete` flag.

This example extends the [complete EF Core provider](./ef-core-provider.md) with workflow state and continuation release logic.

## Extend the job record

Replace the baseline `JobRecord` with this version, then create a migration for the new fields.

```csharp title="JobRecord.cs"
public sealed class JobRecord : IJobStorageRecord
{
    // Required by IJobStorageRecord
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; } = null!;
    public string MethodName { get; set; } = null!;
    public string ArgumentsJson { get; set; } = null!;

    // Added for continuation behavior
    public Guid? ParentJobId { get; set; }
    public JobState State { get; set; } = JobState.Ready;
}

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

Add this new static extension class to your application. `ContinueWithAsync` constructs a normal GUSTO record, associates it with the parent, and stores it in the waiting state.

```csharp title="ContinuationQueueExtensions.cs"
using System.Linq.Expressions;
using ByteBard.GUSTO;

public static class ContinuationQueueExtensions
{
    public static async Task<Guid> ContinueWithAsync(
        this JobQueue<JobRecord> queue,
        Guid parentJobId,
        Expression<Func<Task>> call,
        CancellationToken cancellationToken = default)
    {
        var record = queue.ConstructRecordFromExpression(
            call,
            executeAfter: null);

        record.ParentJobId = parentJobId;
        record.State = JobState.WaitingForParent;

        await queue.StorageProvider.StoreJobAsync(
            record,
            cancellationToken);

        return record.TrackingId;
    }
}
```

Use it after enqueueing the parent job:

```csharp
var parentId = await queue.EnqueueAsync<ImageJobs>(jobs =>
    jobs.RenderAsync(assetId));

await queue.ContinueWithAsync(
    parentId,
    () => publishingJobs.PublishAsync(assetId));
```

## Exclude waiting jobs from the runner

Replace `GetBatchAsync` in `EfCoreJobStorageProvider` with this version. The added state filter prevents the runner from receiving a continuation before its parent completes.

```csharp title="EfCoreJobStorageProvider.cs"
public async Task<IEnumerable<JobRecord>> GetBatchAsync(
    JobSearchParams<JobRecord> search,
    CancellationToken cancellationToken)
{
    return await db.Jobs
        .Where(search.Match)
        .Where(job => job.State == JobState.Ready)
        .OrderBy(job => job.CreatedOn)
        .Take(search.Limit)
        .ToListAsync(cancellationToken);
}
```

## Updating continuations after completion

Replace `MarkJobAsCompleteAsync` in `EfCoreJobStorageProvider` with this version. It marks the parent complete and releases its waiting children in the same transaction.

```csharp title="EfCoreJobStorageProvider.cs"
public async Task MarkJobAsCompleteAsync(
    JobRecord record,
    CancellationToken cancellationToken)
{
    await using var transaction =
        await db.Database.BeginTransactionAsync(cancellationToken);

    var parent = await db.Jobs.SingleOrDefaultAsync(
        job => job.TrackingId == record.TrackingId,
        cancellationToken);

    if (parent is null)
    {
        return;
    }

    parent.IsComplete = true;
    parent.State = JobState.Succeeded;
    await db.SaveChangesAsync(cancellationToken);

    await db.Jobs
        .Where(job =>
            job.ParentJobId == parent.TrackingId &&
            job.State == JobState.WaitingForParent)
        .ExecuteUpdateAsync(update => update
            .SetProperty(job => job.State, JobState.Ready)
            .SetProperty(job => job.ExecuteAfter, DateTime.UtcNow),
            cancellationToken);

    await transaction.CommitAsync(cancellationToken);
}
```

Define the failure branch too. A child might be cancelled, moved to failed state, or released despite parent failure. Leaving it waiting forever is usually the least useful accidental policy.

## Batch continuations

A continuation can wait for every job in a batch instead of one parent job. This requires a batch parent field and concurrency control around completion because several batch members may finish at the same time.

See the [batch continuation example](./batch-continuations.md) for the complete combined record, queue helper, and provider implementation.
