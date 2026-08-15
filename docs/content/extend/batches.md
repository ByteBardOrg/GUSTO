---
title: Batch example
sidebar_position: 2
description: Group independently executed jobs under an application-owned batch ID.
---

A batch can begin as correlation rather than orchestration. Add a nullable identifier to the record, construct several records, and store them with the same value.

```csharp
public Guid? BatchId { get; set; }
```

```csharp
public static async Task<Guid> EnqueueBatchAsync(
    this JobQueue<JobRecord> queue,
    IEnumerable<Expression<Func<Task>>> calls,
    CancellationToken cancellationToken = default)
{
    var batchId = Guid.NewGuid();

    foreach (var call in calls)
    {
        var record = queue.ConstructRecordFromExpression(call, executeAfter: null);
        record.BatchId = batchId;
        await queue.StorageProvider.StoreJobAsync(record, cancellationToken);
    }

    return batchId;
}
```

The worker still sees independent jobs. `BatchId` lets your application query progress, attach logs, or find all records in a workflow.

## Partial batch creation

The helper stores one record at a time. A failure can therefore leave a partial batch. If “all records exist or none do” matters, move batch insertion into a provider-specific transaction or add a bulk operation outside the GUSTO interface.

## Execution order and concurrency

Jobs with the same batch ID may run concurrently, sequentially, or on different workers depending on claiming, ordering, and configured concurrency. A batch ID is correlation metadata; it is not a scheduling primitive by itself.

If downstream work must wait for all members, model that as an explicit continuation with a transactional release condition.
