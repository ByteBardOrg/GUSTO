---
title: Batch continuation example
sidebar_position: 4
description: Run a continuation after every job in a batch completes successfully.
---

This example combines the [batch](./batches.md) and [continuation](./continuations.md) examples. It uses the [EF Core PostgreSQL provider](./ef-core-provider.md) as its baseline.

The continuation remains in `WaitingForParent` until every job with the matching `BatchId` completes successfully. If a batch member reaches terminal failure, the continuation is marked failed and does not run.

## Use the combined job record

Replace the baseline `JobRecord` with this version. It includes the fields required by both examples and an attempt counter for the failure policy shown later on this page.

```csharp title="JobRecord.cs"
using ByteBard.GUSTO;

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

    // Added for batching
    public Guid? BatchId { get; set; }

    // Added for single-job continuations
    public Guid? ParentJobId { get; set; }

    // Added for batch continuations
    public Guid? ParentBatchId { get; set; }

    // Added for continuation and failure behavior
    public JobState State { get; set; } = JobState.Ready;
    public int AttemptCount { get; set; }
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

Create an EF Core migration after changing the record:

```bash
dotnet ef migrations add AddBatchContinuations
dotnet ef database update
```

## Add the queue extensions

Use this complete extension class in place of the separate helpers from the batch and continuation pages. `ContinueAfterBatchAsync` stores the continuation with `ParentBatchId` set to the ID returned by `EnqueueBatchAsync`.

```csharp title="JobQueueExtensions.cs"
using System.Linq.Expressions;
using ByteBard.GUSTO;

public static class JobQueueExtensions
{
    public static async Task<Guid> EnqueueBatchAsync(
        this JobQueue<JobRecord> queue,
        IEnumerable<Expression<Func<Task>>> calls,
        CancellationToken cancellationToken = default)
    {
        var batchId = Guid.NewGuid();

        foreach (var call in calls)
        {
            var record = queue.ConstructRecordFromExpression(
                call,
                executeAfter: null);

            record.BatchId = batchId;
            await queue.StorageProvider.StoreJobAsync(
                record,
                cancellationToken);
        }

        return batchId;
    }

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

    public static async Task<Guid> ContinueAfterBatchAsync(
        this JobQueue<JobRecord> queue,
        Guid parentBatchId,
        Expression<Func<Task>> call,
        CancellationToken cancellationToken = default)
    {
        var record = queue.ConstructRecordFromExpression(
            call,
            executeAfter: null);

        record.ParentBatchId = parentBatchId;
        record.State = JobState.WaitingForParent;

        await queue.StorageProvider.StoreJobAsync(
            record,
            cancellationToken);

        return record.TrackingId;
    }
}
```

## Enqueue the workflow

Enqueue the batch first, then use its ID as the parent of the continuation:

```csharp
var batchId = await queue.EnqueueBatchAsync([
    () => onboarding.CreateDirectoryEntryAsync(customerId),
    () => billing.ProvisionPlanAsync(customerId),
    () => messaging.SendWelcomeAsync(customerId)
]);

await queue.ContinueAfterBatchAsync(
    batchId,
    () => onboarding.MarkCompleteAsync(customerId));
```

The first three jobs are eligible immediately. `MarkCompleteAsync` remains in `WaitingForParent` and is excluded by the provider query below.

## Update the provider query

Replace `GetBatchAsync` in `EfCoreJobStorageProvider`. The state filter excludes both single-job and batch continuations until they are released.

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

## Update successful completion

Replace `MarkJobAsCompleteAsync` with this version. It marks the current job successful, releases a direct child, and releases a batch continuation when no unsuccessful or incomplete batch members remain.

The PostgreSQL advisory transaction lock serializes completion for one `BatchId`. Without it, the last two members could finish concurrently, each observe the other as incomplete, and leave the continuation waiting.

```csharp title="EfCoreJobStorageProvider.cs"
public async Task MarkJobAsCompleteAsync(
    JobRecord record,
    CancellationToken cancellationToken)
{
    await using var transaction =
        await db.Database.BeginTransactionAsync(cancellationToken);

    await LockBatchAsync(record.BatchId, cancellationToken);

    var job = await db.Jobs.SingleOrDefaultAsync(
        item => item.TrackingId == record.TrackingId,
        cancellationToken);

    if (job is null)
    {
        return;
    }

    job.IsComplete = true;
    job.State = JobState.Succeeded;
    await db.SaveChangesAsync(cancellationToken);

    await db.Jobs
        .Where(child =>
            child.ParentJobId == job.TrackingId &&
            child.State == JobState.WaitingForParent)
        .ExecuteUpdateAsync(update => update
            .SetProperty(child => child.State, JobState.Ready)
            .SetProperty(child => child.ExecuteAfter, DateTime.UtcNow),
            cancellationToken);

    if (job.BatchId is Guid batchId)
    {
        var batchHasUnsuccessfulJobs = await db.Jobs.AnyAsync(
            member =>
                member.BatchId == batchId &&
                (!member.IsComplete || member.State != JobState.Succeeded),
            cancellationToken);

        if (!batchHasUnsuccessfulJobs)
        {
            await db.Jobs
                .Where(child =>
                    child.ParentBatchId == batchId &&
                    child.State == JobState.WaitingForParent)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(child => child.State, JobState.Ready)
                    .SetProperty(child => child.ExecuteAfter, DateTime.UtcNow),
                    cancellationToken);
        }
    }

    await transaction.CommitAsync(cancellationToken);
}
```

## Update failure handling

Replace `OnHandlerExecutionFailureAsync` with this version. A failed attempt is rescheduled until it reaches the retry limit. Terminal failure closes any continuation waiting on that job or batch.

```csharp title="EfCoreJobStorageProvider.cs"
public async Task OnHandlerExecutionFailureAsync(
    JobRecord record,
    Exception exception,
    CancellationToken cancellationToken)
{
    const int maxAttempts = 5;

    await using var transaction =
        await db.Database.BeginTransactionAsync(cancellationToken);

    await LockBatchAsync(record.BatchId, cancellationToken);

    var job = await db.Jobs.SingleOrDefaultAsync(
        item => item.TrackingId == record.TrackingId,
        cancellationToken);

    if (job is null)
    {
        return;
    }

    job.AttemptCount++;

    if (job.AttemptCount < maxAttempts)
    {
        var delay = TimeSpan.FromSeconds(
            Math.Pow(2, job.AttemptCount - 1));

        job.State = JobState.Ready;
        job.ExecuteAfter = DateTime.UtcNow.Add(delay);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return;
    }

    job.State = JobState.Failed;
    job.IsComplete = true;
    await db.SaveChangesAsync(cancellationToken);

    await db.Jobs
        .Where(child =>
            child.ParentJobId == job.TrackingId &&
            child.State == JobState.WaitingForParent)
        .ExecuteUpdateAsync(update => update
            .SetProperty(child => child.State, JobState.Failed)
            .SetProperty(child => child.IsComplete, true),
            cancellationToken);

    if (job.BatchId is Guid batchId)
    {
        await db.Jobs
            .Where(child =>
                child.ParentBatchId == batchId &&
                child.State == JobState.WaitingForParent)
            .ExecuteUpdateAsync(update => update
                .SetProperty(child => child.State, JobState.Failed)
                .SetProperty(child => child.IsComplete, true),
                cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
}
```

Add the advisory-lock helper to `EfCoreJobStorageProvider`:

```csharp title="EfCoreJobStorageProvider.cs"
private async Task LockBatchAsync(
    Guid? batchId,
    CancellationToken cancellationToken)
{
    if (batchId is not Guid id)
    {
        return;
    }

    await db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 0))",
        cancellationToken);
}
```

`pg_advisory_xact_lock` is PostgreSQL-specific and is released automatically when the transaction ends. For another database, replace it with that database's transaction or row-locking mechanism.
