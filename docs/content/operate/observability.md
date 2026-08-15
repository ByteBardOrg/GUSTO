---
title: OpenTelemetry
sidebar_position: 3
description: Connect GUSTO traces, metrics, and logs to OpenTelemetry.
---

GUSTO publishes an `ActivitySource` and a `Meter` named `ByteBard.GUSTO.JobQueue`. Register both names in your existing OpenTelemetry pipeline.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(GustoTelemetry.ActivitySourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(GustoTelemetry.MeterName)
        .AddOtlpExporter());
```

Exporter packages and endpoint configuration are supplied by your application.

## Traces

| Activity | Scope | Attributes |
| --- | --- | --- |
| `ProcessBatch` | One non-empty batch | `batch.size` |
| `ExecuteJob` | One invocation | `job.tracking_id`, `job.type`, `job.method` |

Failed job activities have error status and record the exception. Successful activities have OK status.

## Metrics

| Instrument | Type | Unit / dimensions |
| --- | --- | --- |
| `gusto.jobs.processed` | Counter | `job.type`, `job.method`, `job.status` |
| `gusto.job.duration` | Histogram | milliseconds; type, method, status |
| `gusto.batch.duration` | Histogram | milliseconds |
| `gusto.batch.size` | Histogram | jobs |

`gusto.jobs.processed` counts both successful and failed attempts. Separate them using the `job.status` dimension.

:::note Cardinality
Tracking IDs appear on traces, not metric dimensions. Keep custom metric dimensions bounded; customer IDs and job IDs can make a telemetry backend expensive and difficult to query.
:::

## Logs

The worker logs lifecycle events, unexpected loop failures, and job execution failures through `ILogger<JobQueueWorker<TRecord>>`. Configure that category in the host's normal logging settings.

Telemetry reports what the worker sees. Queue depth, oldest-ready age, claim lease expiry, and dead-letter counts belong to your provider and should be measured there.
