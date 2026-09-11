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
| `EnqueueJob` (`Producer`) | Record construction and storage | `job.tracking_id`, `job.type`, `job.method` |
| `ProcessBatch` | One non-empty batch | `batch.size` |
| `ExecuteJob` (`Consumer`) | One invocation | `job.tracking_id`, `job.type`, `job.method` |

Failed job activities have error status and record the exception. Successful activities have OK status.

GUSTO automatically stores the enqueue activity's W3C `traceparent` and `tracestate` with the serialized arguments. Every `ExecuteJob` attempt starts an independent root trace and includes an `ActivityLink` to that persisted remote context. Retries therefore create distinct execution traces linked to the same enqueue activity. `ProcessBatch` remains an independent operational trace; jobs without valid persisted context start roots with no links and never inherit the batch activity. Baggage is not persisted.

Propagation uses `System.Diagnostics` and does not require GUSTO to take a dependency on the OpenTelemetry SDK. If no listener is registered, an ambient valid W3C activity is still persisted, and job execution remains functional without tracing.

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
