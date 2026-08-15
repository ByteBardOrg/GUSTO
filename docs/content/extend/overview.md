---
title: Examples overview
sidebar_position: 1
description: Add application-owned orchestration without confusing it with built-in behavior.
---

The base queue knows only whether a record is eligible, complete, or expired. Richer concepts emerge by adding fields to your record and rules to your provider.

:::caution Recipes, not package APIs
The pages in this section describe implementation patterns. `BatchId`, `JobState`, continuation helpers, and recurring schedules are not included in the GUSTO package. Use the ideas selectively and test them against your own store.
:::

## Extension points used by the examples

Most extensions use three public pieces:

1. Add application fields to the type implementing `IJobStorageRecord`.
2. Use `ConstructRecordFromExpression` in a queue extension to create a normal record and set those fields.
3. Apply the new state rules inside your storage provider.

The worker remains unchanged. That is valuable when the rules are specific to your application, but it also means your team owns their correctness and migrations.

## Example prerequisites

Before adding orchestration, establish:

- atomic claiming for every worker process;
- bounded and observable failure handling;
- idempotent handlers;
- a recovery path for interrupted work.

Then add the smallest concept that expresses the workflow:

| Need | Record addition | Provider responsibility |
| --- | --- | --- |
| Correlate parallel work | `BatchId` | Query and report a group |
| Wait for prerequisite work | `ParentJobId`, state | Release or fail waiting records |
| Repeat work | schedule ID and expression | Compute and persist the next occurrence |

These patterns can be combined, but combining them creates a state machine. Write down valid transitions and protect them transactionally rather than relying on a growing set of incidental query filters.
