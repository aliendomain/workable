# Workflows

## Intent

Workflows let a host register a named multi-step orchestration that dispatches existing Workable work definitions and coordinates them with sequencing, parallel dispatch, and join behavior.

## Workflow Model

Workflow definitions are registered on `IWorkSystemBuilder` with `AddWorkflow(...)`.

The workflow step graph is composed from built-in step kinds:

- `DispatchWork`
- `DispatchEach`
- `Parallel`
- `Join`

Workflow definitions also carry:

- authorization metadata
- input and output schemas
- revision and version metadata

Workflow runs are started by workflow name. Callers may also supply an optional workflow `WorkInput`. Child steps either use the static `WorkInput` values configured on the workflow definition or explicitly bind to the workflow-run input. Non-durable workflow run snapshots are in-memory process state. Durable workflows persist run snapshots through the registered `IWorkPersistenceStore`.

## Registering A Workflow

Workflows are registered directly inside one hosted Workable system.

```csharp
services.AddWorkableSystem(builder =>
{
    var prepareDefinition = WorkDefinition.Create("orders.prepare");
    var emailDefinition = WorkDefinition.Create("orders.email");
    var invoiceDefinition = WorkDefinition.Create("orders.invoice");

    builder.AddWork(
        prepareDefinition,
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWork(
        emailDefinition,
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWork(
        invoiceDefinition,
        (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWorkflow(
        WorkflowDefinition.Create(
            "orders.fulfillment",
            description: "Prepare the order, then send email and invoice work in parallel.",
            category: "Orders"),
        workflow => workflow
            .DispatchWork("prepare", prepareDefinition)
            .RunParallel("notify", parallel => parallel
                .DispatchWork("email", emailDefinition)
                .DispatchWork("invoice", invoiceDefinition))
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
var prepareDefinition = WorkDefinition.Create("orders.prepare");
var emailDefinition = WorkDefinition.Create("orders.email");
var invoiceDefinition = WorkDefinition.Create("orders.invoice");

builder.AddWorkflow(
    WorkflowDefinition.Create(
        "orders.fulfillment.durable",
        coordination: WorkflowCoordinationConfiguration.Durable),
    workflow => workflow
        .DispatchWork("prepare", prepareDefinition)
        .RunParallel("notify", parallel => parallel
            .DispatchWork("email", emailDefinition)
            .DispatchWork("invoice", invoiceDefinition))
        .Join("settle"));
```

Durable workflows:

- require a named Workable system so recovery can scope runs consistently across restarts
- persist the latest workflow-run snapshot through `IWorkPersistenceStore`
- upgrade child dispatches to durable queueing so replay can reconnect child workers to the workflow run
- persist workflow-run step transitions and durable child-worker enqueue in one store-defined transaction at each durable dispatch boundary
- persist retained child completion receipts on the workflow run so joins and operator views do not depend on completed child workers remaining queryable forever
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

`DispatchWork(stepName, workDefinition, input)` queues one existing work definition.

- `stepName` is the stable workflow-local step name
- `workDefinition` is the target registered `WorkDefinition`
- `input` is optional static `WorkInput`

`DispatchWorkFromWorkflowInput(stepName, workDefinition)` queues one existing work definition using the input supplied when the workflow run was started. Multiple steps can bind to the same workflow input.

```csharp
builder.AddWorkflow(
    WorkflowDefinition.Create("orders.fulfillment"),
    workflow => workflow
        .DispatchWorkFromWorkflowInput("prepare", prepareDefinition)
        .DispatchWork("notify", emailDefinition));

await workflows.Start(
    "orders.fulfillment",
    requestContext,
    WorkInput.FromValue(new FulfillmentInput("order-123")),
    cancellationToken: cancellationToken);
```

If the queue request is rejected, the workflow fails immediately.

### Dispatch Each

`DispatchEach(stepName, sourceStep, workDefinition, selector)` waits for the referenced earlier step to complete successfully, resolves a JSON array from that step's retained output, and queues one child worker per array element.

- `stepName` is the stable workflow-local step name
- `sourceStep` is the typed reference returned by an earlier `DispatchWork<TOutput>(...)`
- `workDefinition` is the target registered `WorkDefinition`
- `selector` identifies the array inside the source output. `output => output.Items` resolves `/items`; `output => output` selects the root array.

Each array element becomes the `WorkInput` payload for one queued child worker.

`DispatchEach(...)` performs the fan-out queueing before it waits for the expanded children. Canceling one accepted child therefore does not prevent its accepted siblings from starting. Of the three child-cancellation policies, only `CancelWorkflow` responds by requesting cancellation of every remaining outstanding child.

`DispatchEach(...)` also accepts a `canceledChildBehavior` that controls what happens when an expanded child worker is canceled:

- `WorkflowCanceledChildBehavior.Continue` is the default. The canceled child is treated as skipped, and the workflow waits for the remaining children before continuing.
- `WorkflowCanceledChildBehavior.Block` leaves the workflow blocked at its next join or final child wait without canceling the remaining siblings.
- `WorkflowCanceledChildBehavior.CancelWorkflow` cancels the workflow and requests cancellation of its remaining outstanding child workers.

The policy applies only to workers expanded by that `DispatchEach(...)` step. Canceled workers created by ordinary `DispatchWork(...)` steps continue to block the workflow.

The operator view follows the configured policy. With `Continue`, the `DispatchEach` node remains `WaitingOnChildren` while any sibling is active and becomes `Completed` after every sibling has either completed or been canceled; its child summary still reports the canceled count. `Block` reports the node as `Blocked`, and `CancelWorkflow` reports it as `Canceled`.

The cancellation policy is part of the workflow definition fingerprint used for durable recovery. Changing it while an incomplete durable run is retained produces the same definition-mismatch handling as changing the workflow step graph.

```csharp
var loadDefinition = WorkDefinition.Create("orders.load");
var processDefinition = WorkDefinition.Create("orders.process");

builder.AddWork(loadDefinition, (_, _, _) =>
    Task.FromResult(WorkExecutionResult.Success(
        WorkOutput.FromValue(new OrderBatchOutput([new OrderItem("a"), new OrderItem("b")])))));

builder.AddWork(processDefinition, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));

builder.AddWorkflow(
    WorkflowDefinition.Create("orders.dispatch-each.typed"),
    workflow =>
    {
        var load = workflow.DispatchWork<OrderBatchOutput>("load", loadDefinition);
        workflow.DispatchEach(
            "fan-out",
            load,
            processDefinition,
            output => output.Items,
            canceledChildBehavior: WorkflowCanceledChildBehavior.Continue);
    });
```

That authored shape still persists the same replay information, but the source step reference and selector path are now derived from typed values instead of hand-authored strings.

### Run Parallel

`RunParallel(stepName, configure)` groups child dispatch steps that should be accepted together before the workflow moves on.

A parallel section contains child `DispatchWork(...)` steps.

### Join

`Join(stepName)` waits for earlier dispatched child work to settle.

If any outstanding child worker fails, pauses, or is interrupted, the join step completes as blocked. A canceled child also blocks unless its originating `DispatchEach(...)` step configures `Continue` or `CancelWorkflow`. When the blocked child was a failed worker and that worker is later restarted and completes successfully, Workable resumes the workflow automatically. When a completed child worker has already been purged, joins use the workflow's retained child completion receipt.

## Execution Semantics

Top-level workflow steps are processed in registration order.

- a `DispatchWork(...)` step queues child work and records the accepted child worker id on the workflow step snapshot when one exists
- a `DispatchEach(...)` step waits for its source-step outputs, expands the resolved array, and records the accepted child worker ids for the queued fan-out workers
- a `RunParallel(...)` step queues each child dispatch and records their accepted worker ids on the parallel step snapshot
- a `Join(...)` step waits for all outstanding previously dispatched child work to complete before later workflow steps continue
- a durable workflow persists each dispatch or join transition before later recovery depends on it

Workflow completion waits for accepted child work. `Join(...)` is the synchronization point inside the step graph.

`Join(...)` waits for the outstanding child work that has been dispatched before that point in the workflow. After a join completes, the workflow continues with a new outstanding set.

```csharp
var workADefinition = WorkDefinition.Create("work.a");
var workBDefinition = WorkDefinition.Create("work.b");
var workCDefinition = WorkDefinition.Create("work.c");
var workDDefinition = WorkDefinition.Create("work.d");

workflow => workflow
    .DispatchWork("a", workADefinition)
    .RunParallel("fanout", parallel => parallel
        .DispatchWork("b", workBDefinition)
        .DispatchWork("c", workCDefinition))
    .Join("j1")
    .DispatchWork("d", workDDefinition)
    .Join("j2");
```

In that workflow:

- `j1` waits for `a`, `b`, and `c`
- `j2` waits for `d`

If a child worker completes as failed, paused, or interrupted, the workflow run completes as `Blocked`. Canceled `DispatchWork(...)` children also block. A canceled `DispatchEach(...)` child follows that step's configured cancellation behavior. An operator can issue `Start` for a blocked workflow; when the blocking cause is a final canceled child, the run blocks again because starting it does not replace that child outcome. A blocked run resumes automatically only when its outstanding failed child workers are restarted and later complete successfully.

Completed child workers and completed workflow runs have separate retention lifetimes. Workable records the child completion receipt on the workflow run before later cleanup depends on it, so worker retention can stay aggressive without breaking joins or workflow status views.

`DispatchEach(...)` uses the retained child completion receipts the same way joins do. That means source workers can still be expanded even after the original child worker has been purged, as long as the workflow run still retains the completion receipt.

## Authorization

Workflow authorization uses the same builder and metadata model as work authorization.

```csharp
var childDefinition = WorkDefinition.Create("sample.child");

builder.AddWorkflow(
    WorkflowDefinition.Create("workflow.demo"),
    workflow => workflow.DispatchWork("dispatch", childDefinition),
    authorize: auth => auth
        .AllowReadToGroups("workflow.read")
        .AllowOperateToGroups("workflow.ops"));
```

Starting a workflow checks workflow operate permission.

## Starting From In-Process Code

In-process services can start workflows through `IWorkflowCommandDispatcher`.

```csharp
var result = await workflows.Start(
    "orders.fulfillment",
    requestContext,
    new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted),
    cancellationToken);
```

To provide data to steps bound with `DispatchWorkFromWorkflowInput(...)`, pass `WorkInput` when starting the workflow.

```csharp
var result = await workflows.Start(
    "orders.fulfillment",
    requestContext,
    WorkInput.FromValue(new FulfillmentInput("order-123")),
    new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted),
    cancellationToken);
```

The dispatcher resolves the target Workable system, applies workflow operate authorization using the supplied `WorkRequestContext`, starts the workflow by name, and can either return after acceptance or wait for terminal workflow completion.

ASP.NET Core endpoints can use `IHttpContextWorkflowCommandDispatcher` to build the request context from the current `HttpContext`.

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

Durable workflows persist run state and retained child completion receipts through `IWorkPersistenceStore`. On startup, Workable lists durable workflow runs for the same named system, rehydrates the runs that still have retained lifetime, resumes waiting joins or trailing child-work completion from the persisted step snapshots, and auto-resumes recovered blocked runs when their outstanding failed child workers have already been corrected successfully.

Accepted pause and cancel requests on durable workflows are stored on the persisted run snapshot. If a process recycles after the action is accepted but before the execution loop observes it, recovery reapplies the stored control request before workflow execution resumes.

Final workflow runs remain visible while their child workers are still retained. Once the last child worker for that final run is purged, Workable removes the workflow run too. Durable providers keep the persisted workflow row for that same lifetime and delete it when the final run no longer has any retained child workers.

## Workflow Run Lifecycle

Workflow runs use these public statuses:

- `Running`
- `Paused`
- `Blocked`
- `Completed`
- `Failed`
- `Canceled`

`Paused` means the workflow accepted a pause request and stopped before dispatching later steps. `Blocked` means one or more child workers settled unsuccessfully, or a canceled `DispatchEach(...)` child used the `Block` policy. Failed child workers that are restarted and later complete successfully cause the workflow to resume automatically. A canceled child is already final, so starting a workflow blocked by that cancellation does not make the child successful; an operator can leave the run blocked for inspection or cancel the workflow run.

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

When a completed child worker has already been purged, the workflow views continue to show its resolved state from the retained child completion receipt.

`currentStepName` and `currentStepStatus` identify the first active top-level workflow node in registration order. A parallel node can remain active while a later join node is also waiting on the same child workers.

### HTTP

The HTTP API exposes workflow run status with:

- `GET /workable/workflow-runs`
- `GET /workable/workflow-runs/{runId}`
- `POST /workable/workflows/{workflowName}`
- `POST /workable/workflow-runs/{runId}/actions/start`
- `POST /workable/workflow-runs/{runId}/actions/pause`
- `POST /workable/workflow-runs/{runId}/actions/cancel`

When starting a workflow over HTTP, the request body can include `input` for steps bound with `DispatchWorkFromWorkflowInput(...)`.

```json
{
  "input": {
    "orderId": "order-123"
  },
  "completion": "WaitForCompletion"
}
```

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
