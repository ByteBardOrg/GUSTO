---
title: Failure handling
sidebar_position: 2
description: Implement explicit retry and terminal failure behavior in the storage provider.
---

When a handler throws or times out, GUSTO logs the exception and calls `OnHandlerExecutionFailureAsync`. The provider must leave the record in a deliberate state.

## Failure fields

The base interface has no attempt counter or failure status. Add them to your record:

```csharp
public int AttemptCount { get; set; }
public DateTime? LastFailedOn { get; set; }
public string? LastFailure { get; set; }
public JobState State { get; set; } = JobState.Ready;

public enum JobState
{
    Ready,
    Running,
    Succeeded,
    Failed
}
```

These are application fields, not GUSTO requirements. A simpler provider can use only `ExecuteAfter` and `IsComplete`.

## Retry limit and delay

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
    job.LastFailedOn = DateTime.UtcNow;
    job.LastFailure = exception.ToString();

    if (job.AttemptCount >= 5)
    {
        job.State = JobState.Failed;
        job.IsComplete = true; // Exclude it from the baseline worker predicate.
    }
    else
    {
        var seconds = Math.Pow(2, job.AttemptCount - 1);
        var jitter = Random.Shared.NextDouble();
        job.State = JobState.Ready;
        job.ExecuteAfter = DateTime.UtcNow.AddSeconds(seconds + jitter);
    }

    await db.SaveChangesAsync(cancellationToken);
}
```

The example caps retries and marks terminal failures complete because the worker's baseline predicate understands `IsComplete`. The separate `State` preserves the distinction between success and terminal failure for operators.

## Transient and permanent failures

Not every failure is transient. Validation errors, missing method types after a deployment, and incompatible serialized arguments will not improve with another attempt. Consider classifying known exception types and moving those records directly to terminal failure.

Persist enough context to investigate, but review exception text before storing it: messages can contain customer data or secrets.

## Idempotent handlers

Retries can repeat work. Pass a stable business identifier into the handler, use unique constraints where possible, and make external requests with idempotency keys. Backoff reduces pressure; it does not prevent duplicate effects.
