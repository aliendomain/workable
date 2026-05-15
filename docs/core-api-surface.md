# Core API Surface

## Intent

The core API defines the public shape of Workable for discovering work, queueing work, operating workers, querying state, and observing work events. Consumer-facing contracts live in `Workable.Abstractions`; host setup and the in-process runtime live in `Workable`.

## System Shape

`IWorkSystem` is a small faceted root. It exposes lifecycle and status directly, then delegates the rest of the surface to focused services.

- `Id`, `Name`, and `State` expose system identity and lifecycle state.
- `IWorkCatalog` exposes available work definitions.
- `IWorkQueueService` accepts work by explicit identity.
- `IWorkerOperations` controls worker actions.
- `IWorkQueryService` exposes the discoverable query facade. Each built-in query has a named method, with optional criteria, scope, and cancellation where applicable.
- `IWorkEventStream` creates event subscriptions.
- `Start` and `Stop` control system lifecycle.
- `Stop` returns the shutdown grace period plus workers that were force-canceled because the grace period elapsed, including compact worker summaries and definition names.
- `Stop` clears in-memory worker and iteration records after shutdown cancellation completes.
- `IWorkSystem` is asynchronously disposable.

`IWorkSystemRegistry` exposes the default system, lookup by `WorkSystemId`, optional lookup by name, and enumeration of registered systems.

Work execution receives scoped services and profile access through `IWorkExecutionContext`.
Execution context also exposes the worker's `WorkOrigin`.

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

## Work Identity And Grouping

- `WorkDefinitionId` identifies what work should run.
- `WorkSubjectId` identifies the business subject of a worker, such as a user, order, customer, or cache key.
- `WorkSubjectId` can be used for lookup, correlation, and idempotency.
- `WorkConcurrencyKey` groups workers for concurrency capacity when concurrency is configured by key.
- Subjects and concurrency keys are supplied through `WorkInput`.
- Subjects and concurrency keys are not inferred from the input CLR type.
- A subject does not imply duplicate prevention unless idempotency is enabled.
- A concurrency key does not limit execution unless concurrency is enabled with `PerConcurrencyKey`.

## Queue Rules

- Queue work by passing a `WorkDefinitionId` or name to `IWorkQueueService`.
- `IStartupWorkSource` can return startup queue requests after the catalog is ready.
- Starting a stopped system runs automatic starts and startup work sources again without rebuilding work definitions that were already added by work definition sources.
- Queue input can be supplied as `WorkInput` or as a typed CLR value that Workable serializes into `WorkInput`.
- Queueing returns an `IWorkerHandle` with immediate `WorkQueueOutcome` information.
- Worker handles can be awaited as raw `WorkCompletion` or typed `WorkCompletion<TOutput>`.
- Worker actions return `WorkActionOutcome`.
- Bulk worker actions return `WorkerBulkActionOutcome` with one `WorkActionOutcome` per matched worker.
- Worker snapshots expose durable action history for worker actions and reconfiguration attempts that reached an existing worker.
- Worker snapshots expose `CurrentIterationSequence` and `LastIterationSequence` so callers can cheaply locate the active or most recently completed iteration.
- Direct .NET queueing and worker operations use `IDotNetWorkOriginProvider` to create trusted `DotNet` origins.
- Start configuration controls whether queued work starts automatically and when queue calls return control to the caller.
- Idempotency configuration controls whether workers with the same subject are rejected.
- Concurrency configuration controls whether workers share capacity by definition, subject, or concurrency key.
- Worker handles can be awaited for completion and final result details.
- Completed work results are exposed as `WorkOutput`.
- Worker snapshots can expose captured logs and profile snapshots.
- Worker snapshots expose the `WorkOrigin` that queued the worker.
- `IWorkQueryService.Worker` returns a full `WorkerSnapshot`.
- `IWorkQueryService.Workers` returns lightweight `WorkerOverviewItem` rows.
- `IWorkQueryService.WorkerIteration` returns one full `WorkerIterationSnapshot` by worker id and iteration sequence.
- `IWorkQueryService.WorkerIterations` returns lightweight `WorkerIterationOverviewItem` rows.
- `IWorkQueryService.WorkerKeys` and `IWorkQueryService.WorkerKeyTypes` expose searchable subject, concurrency key, and identifier indexes with matching worker overview rows.
- `IWorkQueryService.WorkIterationKeys` and `IWorkQueryService.WorkIterationKeyTypes` expose the same key search shape for worker iteration overview rows.
- `IWorkQueryService.SystemOverview` returns system state, active-or-queued definition count, worker counts by state, current executing iteration count, iteration counts by completion status, common iteration key type facets, the five most recently updated failed workers, and the five most recent failed/completed iterations.
- `IWorkQueryService` also exposes overview slice methods for counts, worker counts, iteration counts, common key types, failed workers with worker counts, failed iterations, and completed iterations.
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
- Event subscriptions can filter by worker id, work definition id, subject id, concurrency key, work identifier, and event type.
- Each subscription owns a bounded event buffer.
- Disposing a subscription or canceling its reader removes it from the stream.

## Worker State Rules

- Workers are queued when work is accepted.
- Workers run when the system begins executing them.
- Workers enter `Waiting` between scheduled iterations.
- Pause requests move running workers through `Pausing` before they become `Paused`.
- Cancel requests move running workers through `Canceling` before they become `Canceled`.
- Workers become `Completed` when execution succeeds.
- Workers become `Failed` when execution returns errors or fails unexpectedly.
- Final workers are `Completed` or `Canceled`.
- Failed workers are not final; they can be started again or canceled.
- Purging removes a final worker from memory.

## Worker Action Rules

- `Start` applies to `Queued`, `Paused`, and `Failed` workers.
- `Pause` applies to `Running` and `Waiting` workers.
- `Cancel` applies to non-final workers.
- `Push` applies to `Waiting` workers.
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
- Message structure includes code, severity, text, optional target, and optional metadata.
