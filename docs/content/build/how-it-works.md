---
title: Core concepts
sidebar_position: 1
description: The record, provider, queue, and runner used by GUSTO.
---

GUSTO provides the runner and four public types that connect it to your application.

## Job record

Your job record implements `IJobStorageRecord`. GUSTO uses its standard fields to identify the method, store its arguments, and decide when it can run.

The record is also an extension point. Add fields for anything your application needs:

```csharp
public sealed class JobRecord : IJobStorageRecord
{
    // IJobStorageRecord fields...

    public string? TenantId { get; set; }
    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public Guid? BatchId { get; set; }
}
```

GUSTO does not require a particular database model or prevent you from adding application-specific state.

## Storage provider

Your provider implements `IJobStorageProvider<TRecord>`. It stores jobs, returns work to the runner, records completion, cancels jobs, and handles failures.

This is where queue policies are implemented. The provider can apply priorities, tenant rules, retries, locking, archival, or any other behavior supported by your store.

See the [provider call flow](./storage-provider.md#provider-call-flow) for the order in which the queue and runner use these methods.

## Job queue

`JobQueue<TRecord>` creates and stores jobs from strongly typed method calls:

```csharp
await queue.EnqueueAsync<EmailJobs>(jobs =>
    jobs.SendWelcomeEmailAsync(userId));
```

It also exposes `ConstructRecordFromExpression` and the configured storage provider. These members are used to build helpers such as the batch and continuation examples in this documentation.

## Runner

`AddGusto` registers a hosted service that fetches eligible records and executes them. Concurrency, polling, and execution timeouts are configurable. Each job receives its own dependency injection scope.

The runner does not need to change when fields or policies are added to your record and provider.

Next, [run the first job](./first-job.md) with an in-memory provider.
