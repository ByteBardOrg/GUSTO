---
title: Recurring job example
sidebar_position: 4
description: Build recurring execution as provider-owned rescheduling.
---

GUSTO can run a record whose `ExecuteAfter` is in the future. It does not parse cron expressions or create recurring records. You can add that behavior by making recurrence part of your application model.

```csharp
public string? ScheduleId { get; set; }
public string? CronExpression { get; set; }
```

A scheduling helper can validate the expression with a library such as [Cronos](https://github.com/HangfireIO/Cronos), calculate the first occurrence in UTC, and store an ordinary GUSTO record.

```csharp
var cron = CronExpression.Parse(expression, CronFormat.IncludeSeconds);
var next = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc)
    ?? throw new ArgumentException("The schedule has no next occurrence.");

var record = queue.ConstructRecordFromExpression(call, next);
record.ScheduleId = scheduleId;
record.CronExpression = expression;
await queue.StorageProvider.StoreJobAsync(record, cancellationToken);
```

## Updating the next execution time

In `MarkJobAsCompleteAsync`, a recurring record can be moved to its next occurrence instead of becoming complete:

```csharp
if (job.CronExpression is { Length: > 0 })
{
    var cron = CronExpression.Parse(job.CronExpression, CronFormat.IncludeSeconds);
    var next = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);

    job.ExecuteAfter = next;
    job.IsComplete = next is null;
    await db.SaveChangesAsync(cancellationToken);
    return;
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
