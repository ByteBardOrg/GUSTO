---
title: Enqueue and schedule jobs
sidebar_position: 3
description: Enqueue immediate and scheduled jobs and cancel them by tracking ID.
---

Inject `JobQueue<TRecord>` wherever your application creates background work.

## Enqueue a job

```csharp
public sealed class RegistrationService(JobQueue<JobRecord> queue)
{
    public async Task RegisterAsync(Guid userId)
    {
        await queue.EnqueueAsync<EmailJobs>(jobs =>
            jobs.SendWelcomeEmailAsync(userId));
    }
}
```

Job methods must return `Task`. Their constructor dependencies are resolved when the runner executes the job.

## Track a job

`EnqueueAsync` returns the generated tracking ID after the provider stores the record.

```csharp
var trackingId = await queue.EnqueueAsync<ReportJobs>(jobs =>
    jobs.GenerateAsync(reportId));
```

Store this ID when your application needs to associate the job with another record or cancel it later.

## Schedule a job

Pass `executeAfter` to prevent the job from running before a UTC timestamp:

```csharp
await queue.EnqueueAsync<ReminderJobs>(
    jobs => jobs.SendAsync(reminderId),
    executeAfter: DateTime.UtcNow.AddHours(2));
```

This schedules one execution. See the [recurring job example](../extend/recurring-jobs.md) for an application-defined recurring schedule.

## Cancellation tokens

If a job method accepts a `CancellationToken`, pass `default` when enqueueing it:

```csharp
await queue.EnqueueAsync<ReportJobs>(jobs =>
    jobs.GenerateAsync(reportId, default));
```

The runner supplies the execution token when the job runs. It is cancelled when the host stops or the configured job timeout is reached.

## Cancel a queued job

Cancellation behavior is defined by your provider. Call `CancelJobAsync` with the tracking ID:

```csharp
await queue.StorageProvider.CancelJobAsync(
    trackingId,
    cancellationToken);
```

The provider can delete the record or update its application-specific state.

:::note Persisted jobs and code changes
Pending jobs store the target type, method name, and serialized arguments. Keep those methods compatible while matching jobs remain in storage.
:::
