---
title: Worker test hooks
sidebar_position: 4
description: Use worker barriers to coordinate integration tests deterministically.
---

Polling loops often lead to slow tests built around arbitrary delays. GUSTO exposes two static barriers on `JobQueueWorker<TRecord>` so a test can control one batch cycle.

```csharp
[Fact]
public async Task Worker_executes_an_enqueued_job()
{
    var started = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var completed = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);

    JobQueueWorker<JobRecord>.BatchStartBarrier = started;
    JobQueueWorker<JobRecord>.BatchCompletedBarrier = completed;

    try
    {
        await queue.EnqueueAsync<ExampleJobs>(jobs => jobs.DoWorkAsync("test"));

        await hostedService.StartAsync(CancellationToken.None);
        started.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(provider.WasCompleted);
    }
    finally
    {
        await hostedService.StopAsync(CancellationToken.None);
        JobQueueWorker<JobRecord>.BatchStartBarrier = null;
        JobQueueWorker<JobRecord>.BatchCompletedBarrier = null;
    }
}
```

`BatchStartBarrier` pauses before polling. `BatchCompletedBarrier` is signaled after the complete batch cycle, including an empty poll. Each barrier is cleared by the worker after use.

## Isolate barrier tests

The barriers are static per record type. Tests using the same `TRecord` can interfere if they run in parallel. Put them in a non-parallel test collection or use a distinct record type per fixture.

Always put an upper bound around waiting and clear barriers in `finally`. A failed assertion should not leave a shared test process blocked.

## Test the provider separately

Worker integration tests do not prove distributed claiming or retry transitions. Exercise provider behavior at its real database boundary, including:

- two concurrent claims do not return the same record;
- an expired claim becomes recoverable;
- completed and expired records are excluded;
- retry limits become terminal;
- cancellation is idempotent.
