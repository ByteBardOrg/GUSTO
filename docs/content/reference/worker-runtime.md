---
title: Worker runtime behavior
sidebar_position: 3
description: Eligibility, concurrency, timeouts, and storage callbacks.
---

`AddGusto` registers one `JobQueueWorker<TStorageRecord>` with the .NET host.

## Polling

The worker supplies this eligibility expression to `GetBatchAsync`:

```csharp
!job.IsComplete &&
job.ExecuteAfter <= DateTime.UtcNow &&
(job.ExpireOn == null || job.ExpireOn > DateTime.UtcNow)
```

A null `ExecuteAfter` does not match. The provider must apply the expression and the supplied batch limit.

After an empty result, the worker waits for `PollInterval`. After a non-empty batch, it requests another batch.

## Dependency injection scopes

Each job executes in its own scope. Job constructor dependencies and the storage provider can therefore use scoped services such as an EF Core `DbContext`.

## Concurrency

`Concurrency` sets the maximum parallel executions in one runner instance. Coordination between several instances is implemented by the storage provider.

## Completion

After a handler finishes, the worker calls `MarkJobAsCompleteAsync`. The worker does not set `IsComplete`; the provider must persist the completed state.

## Failure

Execution errors and timeouts are passed to `OnHandlerExecutionFailureAsync`. Retry and terminal failure behavior are implemented by the provider.

## Timeout and shutdown

Each job receives a token linked to host shutdown and `JobExecutionTimeout`. The worker replaces parameters of type `CancellationToken` with that token before invocation.

On timeout, the provider receives a `TimeoutException`. Cancellation is cooperative, so job methods and their dependencies should observe the supplied token.

See [Worker test hooks](../operate/testing.md) for the test synchronization API.
