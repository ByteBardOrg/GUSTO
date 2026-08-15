---
title: Configuration
sidebar_position: 1
description: Configure polling, batch size, concurrency, and execution timeout.
---

GUSTO reads its settings from the `Gusto` configuration section.

```json title="appsettings.json"
{
  "Gusto": {
    "BatchSize": 20,
    "Concurrency": 4,
    "PollInterval": "00:00:05",
    "JobExecutionTimeout": "00:05:00"
  }
}
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `BatchSize` | `10` | Maximum records requested per poll |
| `Concurrency` | processor count | Maximum jobs executed at once in this process |
| `PollInterval` | 10 seconds | Delay after an empty poll |
| `JobExecutionTimeout` | 5 minutes | Time allowed for one invocation |

## Batch size and concurrency

`BatchSize` controls how much work is fetched. `Concurrency` controls how many fetched jobs run simultaneously. A batch of 20 with concurrency 4 runs in groups of at most four.

Each job executes in its own dependency injection scope. Account for database connection pools and downstream rate limits when raising concurrency.

## Timeouts and cancellation

The timeout stops waiting for a handler and sends a `TimeoutException` to `OnHandlerExecutionFailureAsync`. If a handler accepts a `CancellationToken`, GUSTO supplies the linked timeout and shutdown token:

```csharp
public async Task RebuildIndexAsync(
    Guid indexId,
    CancellationToken cancellationToken)
{
    await indexer.RebuildAsync(indexId, cancellationToken);
}

await queue.EnqueueAsync<IndexJobs>(jobs =>
    jobs.RebuildIndexAsync(indexId, default));
```

Cancellation is cooperative. Code that ignores the token may continue running after GUSTO reports a timeout. Design handlers and external calls to observe it.
