---
id: introduction
title: Introduction
slug: /introduction
sidebar_position: 1
description: What GUSTO does, what it leaves to you, and when that tradeoff makes sense.
---

GUSTO is a small background job worker for .NET. It serializes a method call into a record, asks your storage provider for runnable records, invokes those methods through dependency injection, and reports the outcome back to the provider.

| | |
| --- | --- |
| Package | [`ByteBard.GUSTO`](https://www.nuget.org/packages/ByteBard.GUSTO) |
| Runtime targets | .NET 8 and .NET 9 |
| Source | [`ByteBardOrg/GUSTO`](https://github.com/ByteBardOrg/GUSTO) |
| License | [MIT](https://github.com/ByteBardOrg/GUSTO/blob/main/LICENSE) |
| Maintainer | ByteBard |

The runner is the part supplied by GUSTO. The storage model and queue behavior are extension points implemented by your application.

## What the package does

- Registers a hosted worker with the .NET host.
- Turns strongly typed method expressions into job records.
- Polls your provider for eligible jobs.
- Runs jobs concurrently, with a configurable timeout.
- Marks successful jobs complete through your provider.
- Passes failed executions back to your provider.
- Emits OpenTelemetry traces and metrics.

## What your application does

- Defines the persisted job record.
- Implements storage and retrieval.
- Makes work claiming safe for the number of workers you run.
- Decides whether failures are retried, abandoned, or dead-lettered.
- Adds concepts such as priority, tenancy, batches, or continuations if needed.

:::info Extension examples
The examples section adds batching, continuations, and recurring jobs by extending the record and provider. They demonstrate how to build on GUSTO without changing the runner.
:::

## Documentation structure

The guide adds one part at a time:

1. [Learn the core concepts](./build/how-it-works.md) and the boundary between library and application.
2. [Run a first job](./build/first-job.md) with the smallest useful record and provider.
3. [Enqueue and schedule jobs](./build/enqueueing.md).
4. [Replace the sample provider](./build/storage-provider.md) with persistence suited to your deployment.
5. Add failure handling, telemetry, tests, and application-specific extensions.

These docs follow the repository's `main` branch. NuGet releases may lag behind it, so check the package version when working with version-sensitive APIs. Every page links to editable source. If the docs and implementation disagree, please [open an issue](https://github.com/ByteBardOrg/GUSTO/issues).
