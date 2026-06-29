# Workflows

## Intent

Workflows let a host register a named multi-step orchestration that dispatches existing Workable work definitions and coordinates them with sequencing, parallel dispatch, and join behavior.

## Workflow Model

Workflow definitions are registered on `IWorkSystemBuilder` with `AddWorkflow(...)`.

The workflow step graph is composed from built-in step kinds:

- `DispatchWork`
- `Parallel`
- `Join`

Workflow definitions also carry:

- authorization metadata
- input and output schemas
- revision and version metadata

Workflow runs are started by workflow name. Child steps use the `WorkInput` values configured on the workflow definition. Non-durable workflow run snapshots are in-memory process state. Durable workflows persist run snapshots through the registered `IWorkPersistenceStore`.

## Registering A Workflow

Workflows are registered directly inside one hosted Workable system.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork(
        WorkDefinition.Create("orders.prepare"),
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWork(
        WorkDefinition.Create("orders.email"),
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWork(
        WorkDefinition.Create("orders.invoice"),
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWorkflow(
        WorkflowDefinition.Create(
            "orders.fulfillment",
            description: "Prepare the order, then send email and invoice work in parallel.",
            category: "Orders"),
        workflow => workflow
            .DispatchWork("prepare", "orders.prepare")
            .RunParallel("notify", parallel => parallel
                .DispatchWork("email", "orders.email")
                .DispatchWork("invoice", "orders.invoice"))
            .Join("settle"),
        authorize: auth => auth
            .AllowReadToGroups("orders.read")
            .AllowOperateToGroups("orders.ops"));
});
```

Workflow names must be unique within one system and are matched case-insensitively.

## Durable Workflows

Durable workflows opt in through `WorkflowCoordinationConfiguration.Durable`.

```csharp
builder.AddWorkflow(
    WorkflowDefinition.Create(
        "orders.fulfillment.durable",
        coordination: WorkflowCoordinationConfiguration.Durable),
    workflow => workflow
        .DispatchWork("prepare", "orders.prepare")
        .RunParallel("notify", parallel => parallel
            .DispatchWork("email", "orders.email")
            .DispatchWork("invoice", "orders.invoice"))
        .Join("settle"));
```

Durable workflows:

- require a named Workable system so recovery can scope runs consistently across restarts
- persist the latest workflow-run snapshot through `IWorkPersistenceStore`
- upgrade child dispatches to durable queueing so replay can reconnect child workers to the workflow run
- persist workflow-run step transitions and durable child-worker enqueue in one store-defined transaction at each durable dispatch boundary
- persist accepted workflow pause and cancel requests on the workflow-run snapshot before acknowledging them
- scan for incomplete runs for that named system when the system starts and resume them from the persisted step state

## Definition Model

`WorkflowDefinition` includes:

- `Name`
- `Category`
- `Description`
- `InputSchema`
- `OutputSchema`
- `Authorization`
- `Revision`
- `Version`

## Step Kinds

### Dispatch Work

`DispatchWork(stepName, workDefinitionName, input)` queues one existing work definition.

- `stepName` is the stable workflow-local step name
- `workDefinitionName` is the target registered work definition
- `input` is optional static `WorkInput`

If the queue request is rejected, the workflow fails immediately.

### Run Parallel

`RunParallel(stepName, configure)` groups child dispatch steps that should be accepted together before the workflow moves on.

A parallel section contains child `DispatchWork(...)` steps.

### Join

`Join(stepName)` waits for earlier dispatched child work to settle.

If any outstanding child worker completes unsuccessfully, the join step completes as blocked. When the blocked child was a failed worker and that worker is later restarted and completes successfully, Workable resumes the workflow automatically.

## Execution Semantics

Top-level workflow steps are processed in registration order.

- a `DispatchWork(...)` step queues child work and records the accepted child worker id on the workflow step snapshot when one exists
- a `RunParallel(...)` step queues each child dispatch and records their accepted worker ids on the parallel step snapshot
- a `Join(...)` step waits for all outstanding previously dispatched child work to complete before later workflow steps continue
- a durable workflow persists each dispatch or join transition before later recovery depends on it

Workflow completion waits for accepted child work. `Join(...)` is the synchronization point inside the step graph.

`Join(...)` waits for the outstanding child work that has been dispatched before that point in the workflow. After a join completes, the workflow continues with a new outstanding set.

```csharp
workflow => workflow
    .DispatchWork("a", "work.a")
    .RunParallel("fanout", parallel => parallel
        .DispatchWork("b", "work.b")
        .DispatchWork("c", "work.c"))
    .Join("j1")
    .DispatchWork("d", "work.d")
    .Join("j2");
```

In that workflow:

- `j1` waits for `a`, `b`, and `c`
- `j2` waits for `d`

If a child worker completes as failed, canceled, paused, or interrupted, the workflow run completes as `Blocked`. A blocked workflow run can always be started again manually. It also resumes automatically when its outstanding failed child workers are restarted and later complete successfully.

## Authorization

Workflow authorization uses the same builder and metadata model as work authorization.

```csharp
builder.AddWorkflow(
    WorkflowDefinition.Create("workflow.demo"),
    workflow => workflow.DispatchWork("dispatch", "sample.child"),
    authorize: auth => auth
        .AllowReadToGroups("workflow.read")
        .AllowOperateToGroups("workflow.ops"));
```

Starting a workflow checks workflow operate permission.

## Child Work Provenance

Workflow-started child work stores the caller's actor, origin, and authentication state in `WorkRequestContext` and adds workflow correlation identifiers to the queued `WorkInput`:

- `workflow-run`
- `workflow-definition`
- `workflow-step`

Stored authorization snapshots are not retained.

## Runtime Storage

Non-durable workflow run snapshots and step status are in-memory process state.

Stopping the process clears:

- active non-durable workflow run state
- historical non-durable workflow run snapshots

Durable workflows persist run state through `IWorkPersistenceStore`. On startup, Workable lists incomplete durable workflow runs for the same named system, rehydrates them into memory, resumes waiting joins or trailing child-work completion from the persisted step snapshots, and auto-resumes recovered blocked runs when their outstanding failed child workers have already been corrected successfully.

Accepted pause and cancel requests on durable workflows are stored on the persisted run snapshot. If a process recycles after the action is accepted but before the execution loop observes it, recovery reapplies the stored control request before workflow execution resumes.

## Workflow Run Lifecycle

Workflow runs use these public statuses:

- `Running`
- `Paused`
- `Blocked`
- `Completed`
- `Failed`
- `Canceled`

`Paused` means the workflow accepted a pause request and stopped before dispatching later steps. `Blocked` means one or more child workers settled unsuccessfully and the workflow is waiting for those child workers to be corrected. Failed child workers that are restarted and later complete successfully cause the workflow to resume automatically.

Workflow actions follow the workflow-run status:

- `Pause` applies to `Running` runs
- `Start` applies to `Paused` and `Blocked` runs
- `Cancel` applies to `Running`, `Paused`, and `Blocked` runs

If the persisted workflow definition fingerprint does not match the currently registered workflow definition, Workable marks the recovered run failed and deletes its persisted run snapshot instead of resuming it.

Non-durable workflows cannot dispatch child work whose effective queue configuration enables durable queueing.

## Workflow Run Status Surfaces

Workable exposes operator-oriented workflow run views that summarize the live state of a workflow and the child workers it launched.

The workflow run list view includes:

- run id and workflow definition name
- workflow run status
- start origin and timestamps
- current top-level step
- outstanding child-worker summary by worker state

The workflow run detail view includes:

- the workflow run summary fields
- the top-level workflow step graph
- nested parallel child step nodes
- child-worker summaries and compact child-worker samples per node

Parallel child step nodes are reconstructed from the workflow definition and the child workers' `workflow-step` identifiers. That keeps the detail view aligned with the authored workflow shape while still using the authoritative child worker state at read time.

`currentStepName` and `currentStepStatus` identify the first active top-level workflow node in registration order. A parallel node can remain active while a later join node is also waiting on the same child workers.

### HTTP

The HTTP API exposes workflow run status with:

- `GET /workable/workflow-runs`
- `GET /workable/workflow-runs/{runId}`
- `POST /workable/workflow-runs/{runId}/actions/start`
- `POST /workable/workflow-runs/{runId}/actions/pause`
- `POST /workable/workflow-runs/{runId}/actions/cancel`

`POST /workable/workflow-runs/{runId}/actions/stop` remains available as a compatibility alias for `pause`.

`GET /workable/workflow-runs` accepts:

- `includeFinal`
- `definitionName`
- `childSampleSize`

`GET /workable/workflow-runs/{runId}` accepts:

- `childSampleSize`

### MCP

The MCP adapter exposes matching workflow run queries with:

- `workable_query_workflow_runs`
- `workable_get_workflow_run`
- `workable_start_workflow_run`
- `workable_pause_workflow_run`
- `workable_cancel_workflow`

`workable_stop_workflow` remains available as a compatibility alias for `workable_pause_workflow_run`.

### SignalR

The SignalR adapter exposes matching live workflow operator views through `WatchView`:

- `workflow-runs`
- `workflow-run`

`workflow-runs` uses one `workflowRuns` component and accepts:

- `includeFinal`
- `definitionName`
- `childSampleSize`

`workflow-run` uses one `workflowRun` component and accepts:

- `runId`
- `childSampleSize`

These live views refresh when the workflow runtime changes and when child-worker state changes, so operator screens can show the authored workflow graph while still reflecting authoritative child-worker progress.

## Workflow Events

Workflow runs publish raw events through the normal Workable event stream:

- `workflow.started`
- `workflow.resume`
- `workflow.pause`
- `workflow.cancel`
- `workflow.step.updated`
- `workflow.paused`
- `workflow.blocked`
- `workflow.completed`
- `workflow.failed`
- `workflow.canceled`

The event envelope uses the workflow definition name as `WorkDefinitionName` and includes these identifiers when they apply:

- `workflow-run`
- `workflow-definition`
- `workflow-step`

`workflow.step.updated` is useful for graph refresh triggers, and the final events are useful for list refresh, notifications, or audit-style monitoring.
