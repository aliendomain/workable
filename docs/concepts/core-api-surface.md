# Core API Surface

## Intent

The core API defines the public shape of Workable for discovering work, queueing work, operating workers, querying state, and observing work events. Consumer-facing contracts live in `Workable.Abstractions`; host setup and the in-process runtime live in `Workable`.

## System Shape

`IWorkSystem` is a small faceted root. It exposes lifecycle and status directly, then delegates the rest of the surface to focused services.

- `Id`, `Name`, and `State` expose system identity and lifecycle state.
- `IWorkCatalog` exposes available work definitions.
- `IWorkQueueService` accepts work by explicit identity.
- `IWorkerOperations` controls worker actions.
- `IWorkQueryService` exposes the discoverable query facade. Each built-in query has a named method, with optional criteria and cancellation where applicable.
- `IWorkEventStream` creates event subscriptions.
- `IWorkSystemDiagnostics` exposes runtime diagnostics for queue rejection, read-model projection, retention cleanup, concurrency backlog, durability loops, and idempotency duplicate rejection.
- `Start` and `Stop` control system lifecycle.
- `Stop` returns the shutdown grace period plus workers that were force-completed as interrupted because the grace period elapsed, including compact worker summaries and definition names.
- `Stop` clears in-memory worker and iteration records after shutdown interruption completes.
- `IWorkSystem` is asynchronously disposable.

`IWorkSystemRegistry` exposes the default system, lookup by name for named systems, and enumeration of registered systems.

Work execution receives scoped services and profile access through `IWorkExecutionContext`.
Execution context also exposes the worker's `WorkRequestContext` (including `Origin`), whether interruption is currently being applied through `IsInterrupted`, and the nullable `InterruptionReason` (`Shutdown` or `LeaseLost`).

## Definition Rules

- Work is described by immutable `WorkDefinition` records.
- Definition metadata lives in `WorkDefinitionMetadata`.
- Browsable definition name, category, and optional description can be supplied directly on `WorkDefinition` or through `WorkMetadataAttribute` on executor classes.
- Registering an executor without an explicit `WorkDefinition` requires `WorkMetadataAttribute`.
- Queue input is represented as `WorkInput`.
- Work results are represented as `WorkOutput`.
- `WorkInput` and `WorkOutput` share the serialized data behavior provided by `WorkData`.
- `WorkInput` can include an optional `WorkSubjectId` for business identity and correlation.
- `WorkInput` can include an optional `WorkConcurrencyKey` for capacity grouping.
- `WorkInput` can include arbitrary `WorkIdentifier` values for query and correlation.
- Execution can add discovered `WorkIdentifier` values through `IWorkExecutionContext`.
- Definitions declare default worker options and default runtime configuration.
- Definitions expose a `Revision` and `WorkDefinitionVersion` for optimistic concurrency when changing definition defaults.
- `IWorkCatalog.Reconfigure` can replace a definition's default worker options and default runtime configuration for future workers.
- Queue requests may override worker options and effective runtime configuration for one run.
- Worker options can enable profiling for captured execution profile trees.
- `IWorkDefinitionSource` can add generated definitions while the system is starting.
- Catalogs do not accept new definitions after work definition sources complete and the system starts.
- Work definition names must be unique within one system catalog.
- A work definition can share input schema or CLR argument shape with other definitions.

## Workflow Foundations

- Workflows are registered through `IWorkSystemBuilder.AddWorkflow(...)`.
- `WorkflowDefinition` mirrors work-definition metadata with name, category, description, schemas, authorization, revision, and version.
- The workflow step graph supports `DispatchWork`, `Parallel`, and `Join`.
- Workflow steps dispatch existing work definitions; they do not introduce a separate executor implementation model.
- Workflows start by name, forward the original actor, origin, and authentication state from `WorkRequestContext` to child work, and add `workflow-run`, `workflow-definition`, and `workflow-step` identifiers to child work input.
- Persisted workflow runs and queued workers retain actor, origin, and authentication state from `WorkRequestContext`, but do not retain precomputed authorization snapshots.
- Workflow actions are `Start`, `Pause`, and `Cancel`. `Paused` and `Blocked` workflow runs can be started again, and blocked runs also resume automatically when their outstanding failed child workers are restarted and later complete successfully.
- Non-durable workflows keep run state in memory.
- Durable workflows require a named system, persist run state through `IWorkPersistenceStore`, upgrade child dispatches to durable queueing, and resume incomplete runs for that named system during system startup.
- Non-durable workflows cannot dispatch child work whose effective queue configuration enables durable queueing.

## Work Identity And Grouping

- `WorkDefinition.Name` is the caller-facing identity used to queue and query one definition.
- `WorkSubjectId` identifies the business subject of a worker, such as a user, order, customer, or cache key.
- `WorkSubjectId` can be used for lookup, correlation, and idempotency.
- `WorkConcurrencyKey` groups workers for concurrency capacity when concurrency is configured by key.
- Subjects and concurrency keys are supplied through `WorkInput`.
- Subjects and concurrency keys are not inferred from the input CLR type.
- A subject does not imply duplicate prevention unless idempotency is enabled.
- A concurrency key does not limit execution unless concurrency is enabled with `PerConcurrencyKey`.

## Queue Rules

- Queue work by passing the definition name to `IWorkQueueService`.
- `IWorkCommandDispatcher` provides a standardized queue-and-optionally-wait path for callers that want system resolution, session creation, queueing, and completion mapping in one helper.
- `IHttpContextWorkCommandDispatcher` is the ASP.NET Core convenience wrapper for that same path when the current `HttpContext` should define the `WorkRequestContext`.
- `IStartupWorkSource` can return startup queue requests after the catalog is ready.
- Starting a stopped system runs automatic starts and startup work sources again without rebuilding work definitions that were already added by work definition sources.
- Queue input can be supplied as `WorkInput` or as a typed CLR value that Workable serializes into `WorkInput`.
- Queueing returns an `IWorkerHandle` with immediate `WorkQueueOutcome` information.
- Worker handles can be awaited as raw `WorkCompletion` or typed `WorkCompletion<TOutput>`.
- Worker actions return `WorkActionOutcome`.
- Bulk worker actions return `WorkerBulkActionOutcome` with one `WorkActionOutcome` per matched worker.
- Worker snapshots expose durable action history for worker actions and reconfiguration attempts that reached an existing worker, including the associated retained iteration sequence when the action was recorded against a tracked iteration.
- Worker snapshots expose `CurrentIterationSequence` and `LastIterationSequence` so callers can cheaply locate the active or most recently completed iteration.
- Direct `IWorkSystem.Queue` and `IWorkSystem.Workers` calls use `WorkInvocationChannel.InProcess`, an unknown actor, and an unauthenticated request context unless the caller creates a `WorkRequestContext` and works through `IWorkSystem.CreateSession(...)`.
- Start configuration controls whether queued work starts automatically and when queue calls return control to the caller.
- Coordination configuration selects local or persistent coordination state, then enables duplicate protection, capacity limits, durable queueing, and durable completion under that mode.
- Idempotency configuration controls whether workers for the same definition and subject are rejected.
- Concurrency configuration controls whether workers share capacity by definition, subject, or concurrency key.
- Worker handles can be awaited for completion and final result details.
- Completed work results are exposed as `WorkOutput`.
- Worker snapshots can expose captured logs and profile snapshots.
- Worker snapshots expose the `WorkRequestContext` that queued the worker, including its durable `Origin`.
- `IWorkQueryService.Worker` returns a full `WorkerSnapshot`.
- `IWorkQueryService.Worker` and `IWorkQueryService.WorkerIteration` return authoritative retained detail.
- Aggregate and list-style query methods read from the runtime read model; the in-memory model starts empty with the process and is cleared when the system stops.
- Control and correctness paths use live worker records instead of the eventually consistent read model. This includes idempotency checks, concurrency decisions, worker actions, shutdown interruption, retention purge selection, and bulk action execution.
- `IWorkSystem.Diagnostics` exposes queue, read-model, retention, concurrency, durability, and idempotency diagnostics. See [Work Diagnostics](diagnostics.md).
- `IWorkQueryService.Workers` returns lightweight `WorkerOverviewItem` rows.
- `IWorkQueryService.WorkerIteration` returns one full `WorkerIterationSnapshot` by worker id and iteration sequence.
- `IWorkQueryService.WorkerIterations` returns lightweight `WorkerIterationOverviewItem` rows.
- `IWorkQueryService.WorkerKeys` and `IWorkQueryService.WorkerKeyTypes` expose searchable subject, concurrency key, and identifier indexes with matching worker overview rows.
- `IWorkQueryService.WorkIterationKeys` and `IWorkQueryService.WorkIterationKeyTypes` expose the same key search shape for worker iteration overview rows.
- `IWorkQueryService.SystemDetails` exposes the typed whole-system aggregate query, including scoped queue pressure through `OldestQueuedAt`.
- `IWorkQueryService` also exposes system slice methods for throughput, worker counts with queue pressure, iteration counts, common key types, failed workers with worker counts, failed iterations, and completed iterations.
- Worker criteria can filter by definition, definition name, subject id, concurrency key, work identifier, state, selected configuration flags, and timestamps.
- Work definition criteria can filter by id, name, category, and search text.
- `IWorkCatalog.ListByCategory` returns definitions by category path.
- Bulk worker actions can target all workers in a system or workers whose definitions belong to a category path.

## Event Rules

- Subscribe to `IWorkEventStream` before starting the activity you want to observe.
- Events are delivered to subscriptions active at publish time.
- Event streams are exposed from a single `IWorkSystem`.
- Events include the publishing `WorkSystemId`.
- Events can include a `WorkOrigin` for the trusted boundary that caused the event.
- Event subscriptions can filter by worker id, one or more work definition ids, subject id, concurrency key, work identifier, key filters, and one or more event types.
- Worker event payloads are selective and bounded. Use event data for notification, correlation, and realtime incremental updates, and query worker detail for full input, messages, full log history, full iteration history, action history, or profile data. The payloads now include focused overview fields such as latest iteration snapshots for `worker.started`, `worker.completed`, `worker.failed`, `worker.waiting`, `worker.retrying`, and `worker.log`, retained worker-level `logSummary` and `timelineSummary` aggregates on the base worker payload, `retryAttempt` and `configDifferenceCount` on the base worker payload when they are relevant, stable log entry ids on `worker.log`, iteration `sequence` and per-iteration `ordinal` on retained log rows for stable ordering, and retained iteration `attemptCount`, `output`, and `failure` fields when those are available.
- Each subscription owns a bounded event buffer.
- Disposing a subscription or canceling its reader removes it from the stream.

## Worker State Rules

- Common automatic worker progressions are:
  - `Queued -> Running`
  - `Running -> Completed`
  - `Running -> Failed`
  - `Running -> Waiting -> Running` for continued recurrence
  - `Running -> Retrying -> Running` for transient retry
  - `Running -> Pausing -> Paused -> Running` for pause and resume
  - `Running -> Canceling -> Canceled`
  - `Running -> Interrupting -> Interrupted`
- Additional control transitions can change that flow:
  - `Queued -> Paused`, `Waiting -> Paused`, and `Retrying -> Paused`
  - `Waiting -> Queued` and `Retrying -> Queued` through `Push`
  - `Failed -> Running` through `Start`
  - Non-final workers can move to `Canceled` when cancellation is applied
  - `Queued`, `Waiting`, and `Retrying` can move directly to `Interrupted`; `Paused` can also be interrupted when a durable lease is lost
- `Interrupting` is a transitional state used when a running worker is being interrupted cooperatively. Workers that are not currently running do not pass through `Interrupting`.
- Final workers are `Completed` or `Canceled`.
- Failed and interrupted workers are not final. Failed workers can be started again or canceled; interrupted durable workers can be replayed after lease expiry or explicitly canceled when materialized.
- Purging removes a final worker from memory.

## Worker Action Rules

- `Start` applies to `Queued`, `Paused`, and `Failed` workers.
- `Pause` applies to `Queued`, `Running`, `Waiting`, and `Retrying` workers.
- `Cancel` applies to non-final workers.
- `Push` applies to `Waiting` and `Retrying` workers.
- `Purge` applies to final workers.
- Worker snapshots expose a `WorkerVersion` that combines worker id and control revision.
- Worker snapshots expose `StateSequence` for lifecycle progress.
- Worker actions and reconfiguration require the observed `WorkerVersion`.
- A stale control revision returns a conflict outcome.
- Concurrent state changes return a conflict outcome.
- Worker action outcomes include the current worker snapshot when the worker exists.
- Bulk worker actions use the current server-side worker revision for each matched worker and report validation or conflict outcomes per worker.
- Accepted control and configuration changes advance the worker revision.
- Runtime progress advances `StateSequence`.

## Outcome Rules

- Expected validation and state failures return structured messages.
- Exceptions are reserved for bugs, infrastructure failures, or unexpected host/runtime errors.
- Unhandled execution exceptions are logged and can be classified as transient or non-transient by work, system, or app-wide classifiers.
- Message structure includes `occurredAt`, code, severity, text, optional target, and optional metadata.
