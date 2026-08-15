---
title: Expressions and serialization
sidebar_position: 2
description: Requirements for job expressions and compatibility of persisted records.
---

Job methods have a few requirements because GUSTO stores the method call for later execution.

## Supported methods

Job methods must return `Task`. Synchronous and `ValueTask` methods can be called from a `Task`-returning job method when needed.

```csharp
public Task GenerateAsync(Guid reportId)
{
    return reportGenerator.GenerateAsync(reportId);
}

await queue.EnqueueAsync<ReportJobs>(jobs =>
    jobs.GenerateAsync(reportId));
```

Avoid overloaded job method names because persisted jobs identify the method by name.

Use a concrete job class in the generic overload. Its constructor dependencies are resolved from dependency injection.

## Argument evaluation

Argument values are captured and serialized when the job is enqueued. Do not put side effects in argument expressions.

Every `CancellationToken` argument is replaced with `default` before serialization. During execution, the worker replaces it with the job execution token.

## Serialized data

Only your application should be able to write stored job arguments. Do not accept arbitrary serialized job records from untrusted sources.

Persisted jobs depend on the job type, method, and argument types. They can fail after changes such as:

- renaming or moving the job type;
- renaming the method;
- changing the method overloads;
- changing argument types incompatibly;
- removing an assembly required by a serialized argument.

Plan deployment compatibility around the maximum time a job may remain queued. For long-lived queues, retain compatible entry methods or migrate pending records before deploying a breaking change.
