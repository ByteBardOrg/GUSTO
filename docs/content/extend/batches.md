---
title: Batch example
sidebar_position: 2
description: Group independently executed jobs under an application-owned batch ID.
---

A batch can begin as correlation rather than orchestration. Add a nullable identifier to your concrete job record, construct several records, and store them with the same value.

This example extends the [complete EF Core provider](./ef-core-provider.md), but the same record field and queue helper work with any provider.

## Extend the job record

Replace the baseline `JobRecord` with this version, then create a migration for `BatchId`.

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

    // Added for batch correlation
    public Guid? BatchId { get; set; }
}
```

## Add the batch helper

Add this new static extension class to your application. It creates ordinary GUSTO records and assigns the same `BatchId` to each one.

```csharp title="BatchQueueExtensions.cs"
using System.Linq.Expressions;
using ByteBard.GUSTO;

public static class BatchQueueExtensions
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
}
```

Call the new helper with the jobs that belong to the batch:

```csharp
var batchId = await queue.EnqueueBatchAsync([
    () => onboarding.CreateDirectoryEntryAsync(customerId),
    () => billing.ProvisionPlanAsync(customerId),
    () => messaging.SendWelcomeAsync(customerId)
]);
```

The worker still sees independent jobs. `BatchId` lets your application query progress, attach logs, or find all records in a workflow.

A batch can also be used as the parent of a continuation. The continuation remains waiting until every job with the matching `BatchId` has completed successfully. This is optional behavior built on top of batching; see the [batch continuation example](./batch-continuations.md).

## Partial batch creation

The helper stores one record at a time. A failure can therefore leave a partial batch. If “all records exist or none do” matters, move batch insertion into a provider-specific transaction or add a bulk operation outside the GUSTO interface.

## Execution order and concurrency

Jobs with the same batch ID may run concurrently, sequentially, or on different workers depending on claiming, ordering, and configured concurrency. A batch ID is correlation metadata; it is not a scheduling primitive by itself.

If downstream work must wait for all members, model that as an explicit continuation with a transactional release condition.
