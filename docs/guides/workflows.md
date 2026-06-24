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

Workflow runs are started by workflow name. Child steps use the `WorkInput` values configured on the workflow definition. Workflow run snapshots are in-memory process state.

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

If any outstanding child worker completes unsuccessfully, the join step fails and the workflow fails.

## Execution Semantics

Top-level workflow steps are processed in registration order.

- a `DispatchWork(...)` step queues child work and records the accepted child worker id on the workflow step snapshot when one exists
- a `RunParallel(...)` step queues each child dispatch and records their accepted worker ids on the parallel step snapshot
- a `Join(...)` step waits for all outstanding previously dispatched child work to complete before later workflow steps continue

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

If a child worker completes as failed or canceled, the workflow run completes unsuccessfully. Interrupted child work completes the workflow as failed.

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

Child work dispatches reuse the same `WorkRequestContext`.

## Child Work Provenance

Workflow-started child work keeps the caller's `WorkRequestContext` and adds workflow correlation identifiers to the queued `WorkInput`:

- `workflow-run`
- `workflow-definition`
- `workflow-step`

## In-Memory Runtime

Workflow run snapshots and step status are in-memory process state.

Stopping the process clears:

- active workflow run state
- historical workflow run snapshots

Non-durable workflows cannot dispatch child work whose effective queue configuration enables durable queueing.
