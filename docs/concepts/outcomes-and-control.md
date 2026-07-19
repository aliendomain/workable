# Outcomes And Control

Workable exposes a few different outcome records because "accepted the request," "applied the worker action," and "finished the work" are different moments in the lifecycle.

This document ties those results together and explains how control operations fit around them.

## Outcome Families

The main outcome families are:

- `WorkQueueOutcome`: immediate result of queue acceptance
- `WorkActionOutcome`: immediate result of a worker action or worker reconfiguration
- `WorkCompletion`: final result of worker execution
- `WorkDefinitionReconfigurationOutcome`: immediate result of changing definition defaults
- `WorkerBulkActionOutcome`: aggregate result of applying one action across a worker set

Each one describes a different stage and should be read in that stage's terms.

All of them also carry structured `WorkMessage` values so callers can surface validation, authorization, and state-transition detail without parsing exception text. Each message includes `occurredAt`, code, severity, text, and optional target/metadata fields.

## Queue Outcomes

Queueing returns an `IWorkerHandle`. The handle always contains a `WorkQueueOutcome`, even when queueing fails.

`WorkQueueStatus` values are:

- `Accepted`
- `Invalid`
- `Unauthorized`
- `NotFound`

If the status is `Accepted`, the outcome includes the created worker id. The queued definition remains available from the resulting worker snapshot and catalog. If the request is not accepted, the caller still gets structured `WorkMessage` values explaining why.

Think of `WorkQueueOutcome` as "did Workable accept the request and create a worker record?" It does not say anything yet about whether the worker eventually completed successfully.

## Worker Handles And Completion

`IWorkerHandle` bridges queue acceptance and final execution:

- `QueueOutcome` tells you what happened immediately
- `WorkerId` identifies the accepted worker when there is one
- `WaitForCompletion()` waits for the eventual `WorkCompletion`

This is why fire-and-forget and request/response queueing can use the same queue API. A caller can ignore the handle after acceptance or keep it and await completion.

## Completion Outcomes

`WorkCompletion` and `WorkCompletion<TOutput>` describe final worker execution, not queue acceptance.

`WorkCompletionStatus` values are:

- `Executing`
- `Completed`
- `Failed`
- `Paused`
- `Interrupted`
- `Canceled`
- `Invalid`
- `NotFound`

`Executing` is used for iteration filtering and active iteration snapshots. `Paused`, `Invalid`, and `NotFound` are also part of the public enum, though ordinary successful queue-and-wait flows most often return `Completed`, `Failed`, `Canceled`, or `Interrupted`.

The completion result includes:

- the final status
- the final `WorkerSnapshot`, when one exists
- the raw `WorkOutput`
- structured messages

Typed completions add a deserialized `Output` while preserving `RawOutput`.

That distinction is useful when the caller wants a CLR value for business logic but still needs access to the serialized output shape or content type for logging, transport, or inspection tooling.

Use completion when you care about business outcome. Use queue outcome when you care about admission and immediate validation.

## Worker Actions

`IWorkerOperations.Execute(...)` applies one action to one worker version.

`WorkAction` values are:

- `Start`
- `Pause`
- `Cancel`
- `Push`
- `Purge`

`WorkActionOutcome` tells you whether the action request was accepted against the targeted worker revision.

`WorkActionStatus` values are:

- `Accepted`
- `Invalid`
- `Conflict`
- `Unauthorized`
- `NotFound`

The important distinction is:

- `Accepted` means the action was applied to the worker state machine
- it does not mean the worker has already reached its later lifecycle result

For example, `Cancel` can be accepted now and the worker may publish its later canceled completion after that.

`WorkActionOutcome` also includes the current `WorkerSnapshot` when the worker exists. That gives operators and custom UIs a post-action state view without immediately issuing a second query.

## Bulk Actions

`ExecuteAll(...)` applies one action across a matched set and returns `WorkerBulkActionOutcome`.

The bulk result includes:

- the action
- the filter that was used
- matched worker count
- one `WorkActionOutcome` per matched worker
- summary counts for accepted, conflict, invalid, unauthorized, and not found results

Bulk actions are not all-or-nothing. They are intentionally reported per worker so operators can see mixed results in one response.

`WorkerBulkActionFilter` is currently category-oriented:

- `Category`
- `IncludeSubcategories`

Use it for "apply this action to all workers in this category slice" rather than for arbitrary worker queries.

## Reconfiguration Outcomes

There are two kinds of reconfiguration:

- worker reconfiguration through `IWorkerOperations.Reconfigure(...)`
- definition-default reconfiguration through `IWorkCatalog.Reconfigure(...)`

Worker reconfiguration updates one worker's effective options and runtime configuration. It returns `WorkActionOutcome` because it is treated as a worker control operation.

Definition reconfiguration updates defaults for future workers. It returns `WorkDefinitionReconfigurationOutcome`.

The payload shapes are intentionally different:

- `WorkerReconfiguration` changes one existing worker's effective start, coordination, recurrence, retry, failed-worker handling, logging, retention, or profiling settings
- `WorkDefinitionReconfiguration` changes definition defaults for future workers through `DefaultOptions` and `Configuration`

`WorkDefinitionReconfigurationStatus` values are:

- `Accepted`
- `Unauthorized`
- `NotFound`
- `Invalid`
- `Conflict`

## Optimistic Concurrency

Control operations intentionally use optimistic concurrency.

For workers:

- `Execute(...)` and `Reconfigure(...)` require `WorkerVersion`
- stale callers receive `Conflict`

The concise `Execute(worker, action, cancellationToken)` overload applies an action without a reason. Use the `WorkerActionRequest` overload when the action should carry a human-readable reason:

```csharp
await session.Workers.Execute(
    worker.Version,
    new WorkerActionRequest(
        WorkAction.Cancel,
        Reason: "The customer withdrew the order."),
    cancellationToken);
```

The action reason is recorded as the action request context description. For an accepted cancellation of running code, that same context becomes available through `IWorkExecutionContext.CancellationRequestContext` before Workable signals the execution cancellation token.

For definitions:

- `Reconfigure(...)` requires `WorkDefinitionVersion`
- stale callers receive `Conflict`

This prevents one operator or process from silently applying control decisions against an older view of state.

In practice, optimistic concurrency is what makes operator tooling trustworthy. It forces the caller to act on an observed revision instead of assuming no one else touched the worker or definition in the meantime.

## Action History

`WorkerSnapshot.ActionHistory` records action and reconfiguration outcomes that reached an existing worker record.

Each `WorkerActionHistoryEntry` captures:

- when it happened
- whether it was a worker action or reconfiguration
- the `RequestContext` that applied it, including durable `RequestContext.Origin` provenance plus optional request-level `Description` and `Url`
- the action, when applicable
- the resulting `WorkActionStatus`
- the `WorkOrigin`
- the worker revision and state sequence at that time
- structured messages
- the requested worker reconfiguration, when applicable

That history includes both action operations and reconfiguration operations through `WorkerActionHistoryKind`. It is the audit trail that lets a UI explain why a worker was paused, reconfigured, canceled, or conflicted.

## Choosing The Right Result

Use:

- `WorkQueueOutcome` when the caller needs admission and validation information immediately
- `WorkCompletion` when the caller needs final business outcome
- `WorkActionOutcome` when the caller needs to know whether a worker control request applied
- `WorkerBulkActionOutcome` when an operator is acting across a category slice
- `WorkDefinitionReconfigurationOutcome` when changing defaults for future workers

Those result types are different on purpose. They keep queue admission, control application, and execution completion from being collapsed into one ambiguous "status."
