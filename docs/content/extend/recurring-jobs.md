---
title: Recurring job example
sidebar_position: 4
description: Build recurring execution as provider-owned rescheduling.
---

GUSTO can run a record whose `ExecuteAfter` is in the future. It does not parse cron expressions or create recurring records. You can add that behavior by making recurrence part of your application model.

This example extends the [complete EF Core provider](./ef-core-provider.md) and uses [Cronos](https://github.com/HangfireIO/Cronos) to calculate occurrences.

Install Cronos in the application containing the queue helper and provider:

```bash
dotnet add package Cronos
```

## Extend the job record

Replace the baseline `JobRecord` with this version, then create a migration for the schedule fields.

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

    // Added for recurring job behavior
    public string? ScheduleId { get; set; }
    public string? CronExpression { get; set; }
}
```

## Add the scheduling helper

Add this new static extension class to your application. `ScheduleRecurringAsync` calculates the first occurrence and stores it as a normal scheduled GUSTO record.

```csharp title="RecurringJobQueueExtensions.cs"
using System.Linq.Expressions;
using ByteBard.GUSTO;
using Cronos;

public static class RecurringJobQueueExtensions
{
    public static async Task<Guid> ScheduleRecurringAsync(
        this JobQueue<JobRecord> queue,
        string scheduleId,
        Expression<Func<Task>> call,
        string expression,
        CancellationToken cancellationToken = default)
    {
        var cron = CronExpression.Parse(
            expression,
            CronFormat.IncludeSeconds);

        var next = cron.GetNextOccurrence(
            DateTime.UtcNow,
            TimeZoneInfo.Utc)
            ?? throw new ArgumentException(
                "The schedule has no next occurrence.",
                nameof(expression));

        var record = queue.ConstructRecordFromExpression(call, next);
        record.ScheduleId = scheduleId;
        record.CronExpression = expression;

        await queue.StorageProvider.StoreJobAsync(
            record,
            cancellationToken);

        return record.TrackingId;
    }
}
```

Call the new helper with a six-field cron expression:

```csharp
await queue.ScheduleRecurringAsync(
    scheduleId: "nightly-ledger",
    call: () => ledgerJobs.CloseDayAsync(),
    expression: "0 0 2 * * *");
```

## Updating the next execution time

Replace `MarkJobAsCompleteAsync` in `EfCoreJobStorageProvider` with this version. A recurring record is moved to its next occurrence; any other record is marked complete normally.

```csharp title="EfCoreJobStorageProvider.cs"
public async Task MarkJobAsCompleteAsync(
    JobRecord record,
    CancellationToken cancellationToken)
{
    var job = await db.Jobs.SingleOrDefaultAsync(
        item => item.TrackingId == record.TrackingId,
        cancellationToken);

    if (job is null)
    {
        return;
    }

    if (job.CronExpression is { Length: > 0 } expression)
    {
        var cron = CronExpression.Parse(
            expression,
            CronFormat.IncludeSeconds);

        var next = cron.GetNextOccurrence(
            DateTime.UtcNow,
            TimeZoneInfo.Utc);

        job.ExecuteAfter = next;
        job.IsComplete = next is null;
    }
    else
    {
        job.IsComplete = true;
    }

    await db.SaveChangesAsync(cancellationToken);
}
```

## Scheduling behavior

The compact example calculates from completion time. That skips missed occurrences and allows execution duration to shift the schedule. Other valid models calculate from the previous scheduled time, create one record per occurrence, or catch up missed runs.

Also decide:

- which time zone owns the expression and how daylight-saving transitions behave;
- whether deployments may register the same `ScheduleId` idempotently;
- what happens after a failed occurrence;
- whether overlapping occurrences are allowed.

If these policies become broad or operator-facing, a dedicated scheduler may be a better boundary than continuing to extend the provider.
