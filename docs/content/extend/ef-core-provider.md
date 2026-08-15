---
title: EF Core provider
sidebar_position: 2
description: A complete GUSTO storage provider implemented with EF Core.
---

This example stores jobs in PostgreSQL through EF Core. It includes every type required to register the provider and run jobs.

## Packages

```bash
dotnet add package ByteBard.GUSTO
dotnet add package Microsoft.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

Use the EF Core provider package for your database if you are not using PostgreSQL.

## Job record

```csharp title="JobRecord.cs"
using ByteBard.GUSTO;

public sealed class JobRecord : IJobStorageRecord
{
    public Guid TrackingId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ExecuteAfter { get; set; }
    public DateTime? ExpireOn { get; set; }
    public bool IsComplete { get; set; }
    public string JobType { get; set; } = null!;
    public string MethodName { get; set; } = null!;
    public string ArgumentsJson { get; set; } = null!;
}
```

Add application fields to this class when implementing retries, priorities, batches, or other queue behavior.

## DbContext

```csharp title="JobsDbContext.cs"
using Microsoft.EntityFrameworkCore;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options)
    : DbContext(options)
{
    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var jobs = modelBuilder.Entity<JobRecord>();

        jobs.HasKey(job => job.TrackingId);
        jobs.Property(job => job.JobType).IsRequired();
        jobs.Property(job => job.MethodName).IsRequired();
        jobs.Property(job => job.ArgumentsJson).IsRequired();

        jobs.HasIndex(job => new
        {
            job.IsComplete,
            job.ExecuteAfter,
            job.ExpireOn
        });
    }
}
```

## Storage provider

```csharp title="EfCoreJobStorageProvider.cs"
using ByteBard.GUSTO;
using Microsoft.EntityFrameworkCore;

public sealed class EfCoreJobStorageProvider(JobsDbContext db)
    : IJobStorageProvider<JobRecord>
{
    public async Task StoreJobAsync(
        JobRecord record,
        CancellationToken cancellationToken)
    {
        db.Jobs.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<JobRecord>> GetBatchAsync(
        JobSearchParams<JobRecord> search,
        CancellationToken cancellationToken)
    {
        return await db.Jobs
            .Where(search.Match)
            .OrderBy(job => job.CreatedOn)
            .Take(search.Limit)
            .ToListAsync(cancellationToken);
    }

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

        job.IsComplete = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelJobAsync(
        Guid trackingId,
        CancellationToken cancellationToken)
    {
        await db.Jobs
            .Where(job => job.TrackingId == trackingId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task OnHandlerExecutionFailureAsync(
        JobRecord record,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(
            item => item.TrackingId == record.TrackingId,
            cancellationToken);

        if (job is null)
        {
            return;
        }

        // This baseline retries the job after five minutes.
        job.ExecuteAfter = DateTime.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

This is a complete provider with a simple retry policy. See [Failure handling](../operate/failures.md) to add retry limits and a failed state.

## Registration

```csharp title="Program.cs"
builder.Services.AddDbContext<JobsDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Jobs")));

builder.Services.AddGusto<JobRecord, EfCoreJobStorageProvider>(
    builder.Configuration);
```

`AddGusto` registers `EfCoreJobStorageProvider`, so a separate provider registration is not required.

```json title="appsettings.json"
{
  "ConnectionStrings": {
    "Jobs": "Host=localhost;Database=example;Username=postgres;Password=postgres"
  },
  "Gusto": {
    "BatchSize": 20,
    "Concurrency": 4,
    "PollInterval": "00:00:05",
    "JobExecutionTimeout": "00:05:00"
  }
}
```

Create and apply the database migration:

```bash
dotnet ef migrations add AddGustoJobs
dotnet ef database update
```

## Multiple application instances

The query above is sufficient for one runner instance. If several application instances use the same jobs table, update `GetBatchAsync` to claim rows while selecting them. The exact implementation depends on the database and can use row locks or application-defined lease fields.
