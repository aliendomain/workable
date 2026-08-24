# Work Authorization

Workable supports authorization at two levels:

- work-definition authorization controls who can discover, read, or operate individual work definitions
- system authorization controls who can discover a system when they have actual access to it, view diagnostics, control lifecycle, or manage temporary profiling capture rules

The model is request-context based. Callers create or receive a `WorkRequestContext`, Workable creates an `IWorkSystemSession`, and that session exposes caller-scoped discovery, catalog, queue, worker operations, query service, event stream, and diagnostics surfaces.

## Security Model

Systems are authorization-enabled by default. Most hosts do not need to call `RequireAuthorization()` explicitly unless they want the code to say so.

When authorization is enabled on a system:

- work with no authorization configured is closed by default
- discovery surfaces expose only redacted definition descriptors the caller may discover
- read surfaces filter out work the caller cannot read
- queueing and worker operations return unauthorized outcomes when the caller cannot operate the target work
- diagnostics, retained profile telemetry, full-capture selection, and profiling capture-rule management require system-level diagnostics permission
- start and stop require system-level control permission
- system discovery is filtered to systems where the caller has actual access

Turn authorization off only when the system is intentionally open to all callers:

```csharp
services.AddWorkableSystem(builder =>
{
    builder.RequireAuthorization(false);
});
```

This opt-out only applies to direct in-process use of the core runtime. The current transport adapters do not expose open systems. HTTP rejects a mapping that would cover one, MCP rejects an open requested system, and SignalR rejects an open system when a hub method requests it without preventing secured systems in the same host from using realtime. See the transport adapter docs for the exact behavior and constraints.

When `RequireAuthorization(false)` is set:

- work-definition and system-level authorization are not enforced
- direct `.NET` callers can still use `WorkRequestContext` and `IWorkSystemSession`
- authorization metadata remains on the catalog as design-time information

Current adapter behavior is intentionally stricter than the core runtime:

- `Workable.HttpApi` requires authenticated callers and authorization-enabled systems
- `Workable.Mcp` requires authenticated callers and authorization-enabled systems
- `Workable.SignalR` requires authenticated callers and exposes only authorization-enabled systems

## Work Authorization

Each `WorkDefinition` carries non-null authorization metadata:

- discover groups
- read groups
- operate groups
- whether discover, read, or operate access is also allowed to known authenticated users
- the source of each permission set: `None`, `Attribute`, or `Fluent`

That authorization metadata is visible through the full read-authorized catalog and definition queries so readers can inspect what a work definition requires. The redacted discovery catalog does not expose authorization requirements.

### Attribute-Based Authorization

```csharp
[WorkMetadata("billing.invoice.sync", "Billing")]
[WorkAuthorization(
    DiscoverGroups = ["billing.catalog"],
    ReadGroups = ["billing.read", "billing.admin"],
    OperateGroups = ["billing.ops", "billing.admin"])]
public sealed class SyncInvoicesWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

### Fluent Authorization

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<SyncInvoicesWork>(
        configure: null,
        authorize: auth => auth.RequireGroups(
            readGroups: ["billing.read", "billing.admin"],
            operateGroups: ["billing.ops", "billing.admin"]));
});
```

`RequireGroups(...)` clears explicit Discover grants and replaces the Read and Operate requirements with the supplied groups, including removing previously configured known-authenticated-user grants. The resulting Read and Operate audiences still receive effective discovery. Use the surface-specific helpers when grants should be composed instead.

You can also configure the three requirements independently:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth
        .AllowDiscoverToGroups("billing.catalog")
        .AllowReadToGroups("billing.read", "billing.admin")
        .AllowOperateToGroups("billing.ops", "billing.admin"));
```

Discovery exposes a redacted `WorkDefinitionDescriptor`: name, description, category, input/output schemas, and `WorkDefinitionMetadata`. It does not expose runtime configuration, authorization metadata, workers, inputs, outputs, results, history, queries, or events. Read and operate access each imply discovery, so callers who already have either permission do not need a redundant discover grant.

Use explicit discovery for schema-only consumers:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth.AllowDiscoverToKnownAuthenticatedUsers());
```

To let every authenticated caller that resolves to a known `WorkActor` discover the definition, its schemas, and its retained data without granting any operation permission, configure the read side explicitly:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth.AllowReadToKnownAuthenticatedUsers());
```

`AllowReadToKnownAuthenticatedUsers()` can be combined with `AllowReadToGroups(...)`; either audience may then satisfy the read requirement. It does not allow queueing, worker actions, or reconfiguration.

`AllowOperateToGroups(...)` remains the convenience grant for the full work-operation surface: queueing, worker actions, worker reconfiguration, and definition reconfiguration.

When a definition needs finer control, split those concerns explicitly:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth
        .AllowQueueToGroups("billing.queue")
        .AllowWorkerActionsToGroups("billing.ops")
        .AllowOperationsToGroups(
            ["billing.admin"],
            WorkOperationPermissions.Reconfigure));
```

Or allow the same surfaces for callers that are both authenticated and resolved to a known `WorkActor`:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth.AllowQueueToKnownAuthenticatedUsers());
```

You can also add synchronous, input-aware operate requirements to any work-level grant. These extra checks apply only after the caller already satisfies the underlying work-level audience:

```csharp
builder.AddWork<AdminSurveyWork>(
    authorize: auth => auth.AllowOperateToGroups(
        ["survey.admin"],
        operate => operate.WhenOperatingRequire<AdminSurveyArgs>(context =>
            context.Input?.AreaKey == "north")));
```

The same extra constraint shape is available for known authenticated users:

```csharp
builder.AddWork<AdminSurveyWork>(
    authorize: auth => auth.AllowWorkerActionsToKnownAuthenticatedUsers(
        operate => operate.WhenOperatingRequire<AdminSurveyArgs>(context =>
            context.Input?.AreaKey == "north")));
```

When several registrations inside one system share the same work-level authorization, group them with `WithWorkDefaults(...)`:

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<SubmitSurveyWork>(
        authorize: auth => auth.AllowOperateToKnownAuthenticatedUsers());

    builder.WithWorkDefaults(
        register: work => work
            .AddWork<CreateSurveyAreaWork>()
            .AddWork<CreateSurveyTemplateWork>()
            .AddWork<DeleteSurveyAreaWork>()
            .AddWork<UpdateSurveyTemplateWork>(),
        authorize: auth => auth.AllowOperateToGroups("survey.admin"));
});
```

Known-authenticated-user grants and constrained operate requirements are available through the fluent builder API, not through `WorkAuthorizationAttribute`. The attribute supports `DiscoverGroups`, `ReadGroups`, and `OperateGroups`.

This rule is intentionally narrower than "authenticated transport request." The caller must be authenticated and the request context must carry a known actor with at least one non-blank identity field such as `Id`, `Name`, or `Email`.

For built-in ASP.NET Core transports and custom endpoints that use the HTTP-context dispatchers, Workable sets this automatically from `HttpContext`. A lower-level direct `IWorkRequestContextFactory` call reads the already-selected principal, so callers using an explicit transport scheme initialize it through `WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(...)` first. For trusted direct in-process callers that build `WorkRequestContext` values manually, the caller is responsible for setting `isAuthenticated: true` when that meaning is intended.

Fluent authorization overrides attribute authorization.

Within `WithWorkDefaults(...)`, the group-level `authorize` callback runs before any per-work `authorize` callback. That means a specific work can refine or replace the grouped authorization without repeating the shared policy for every sibling registration.

## Delegated Child Execution

An authorized parent may execute explicitly declared child work without requiring the initiating actor to have the child's direct queue permission. This is scoped execution delegation, similar to a caller executing a stored procedure without receiving direct access to its underlying tables.

Declare every allowed parent-to-child edge during work configuration:

```csharp
var child = WorkDefinition.Create("orders.internal.reserve-stock");
var parent = WorkDefinition.Create("orders.place");

builder.AddWork(
    child,
    ReserveStock,
    configure: null,
    authorize: auth => auth.AllowQueueToGroups("inventory.internal"));

builder.AddWork<PlaceOrderExecutor>(
    parent,
    configure: configuration => configuration.AllowChildExecution(child),
    authorize: auth => auth.AllowQueueToGroups("orders.place"));
```

`AllowChildExecution(...)` is a registration-specific authority grant and cannot be placed in
`WithWorkDefaults`. Declare each edge in the individual parent registration's `configure` callback;
Workable rejects a defaults-scoped grant during registration with an explanatory exception.

The executor receives `IChildWorkQueueService` from its execution scope:

```csharp
public sealed class PlaceOrderExecutor(IChildWorkQueueService children) : IWorkExecutor
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        var reservationInput = ValidateAndCreateReservationInput(input);
        var child = await children.Enqueue(
            "orders.internal.reserve-stock",
            reservationInput,
            cancellationToken: cancellationToken);
        var completion = await child.WaitForCompletion(cancellationToken);
        return completion.IsCompletedSuccessfully
            ? WorkExecutionResult.Success()
            : WorkExecutionResult.Failure(completion.Messages);
    }
}
```

The delegation rules are deliberately narrow:

- direct queueing of the child still checks the child's authorization
- the scoped child queue can target only definitions declared by `AllowChildExecution(...)`
- the queue is revoked when the parent execution attempt returns
- the child keeps the initiating actor and origin for authorization and audit
- child input validation, configuration validation, capacity, concurrency, idempotency, and durability still apply
- child-execution relationships are code-defined and cannot be changed through runtime definition reconfiguration
- the relationship graph must be acyclic; catalog startup rejects self-references and reports the path of any cycle

The parent executor is the security boundary for delegated input. It must validate any caller-controlled business
scope before constructing the child input, must not forward arbitrary `WorkerOptions`, and must bound caller-driven
fan-out. Child queue authorization—including requirements that inspect input or options—is intentionally bypassed.
Workable still applies the child's ordinary input and configuration validation, but it cannot enforce application-level
rules such as which account, tenant, or order the parent is permitted to affect.

Each edge in a nested chain must be declared. Permission to execute parent A can reach child C only when A declares B and B independently declares C. The initiating actor never receives a general session that can queue B or C directly.

Workflow dispatch edges are declarations by construction. Once a caller is authorized to start a workflow, its `DispatchWork(...)` and `DispatchEach(...)` steps can execute their declared child definitions without separate child queue permission. Direct child reads and direct worker controls remain independently authorized. Workflow pause, resume, and cancel may propagate to that run's own outstanding children without granting the caller direct control of those workers.

Before propagating workflow pause, resume, or cancel with delegated authority, Workable revalidates the
authoritative worker snapshot. Its system-assigned workflow provenance must match the current run, definition,
and step, and that run must record the worker under the same step. The system-reserved `workflow-run` identifier
remains searchable correlation metadata and is never accepted as authority. A provenance mismatch falls back to the caller's
ordinary authorized worker operations instead of using delegated control.

Public worker query and realtime projections expose only the trusted workflow run id. The workflow definition and
step portions of provenance stay inside the runtime and trusted persistence-provider boundary so child-worker read
permission does not disclose workflow structure. Reading the referenced run remains independently authorized.

## Workflow Authorization

Workflow definitions use the same `WorkDefinitionAuthorization` metadata shape and the same `IWorkAuthorizationBuilder` fluent model as work definitions.

```csharp
var childDefinition = WorkDefinition.Create("sample.child");

builder.AddWork(childDefinition, (_, _, _) =>
    Task.FromResult(WorkExecutionResult.Success()));

builder.AddWorkflow(
    WorkflowDefinition.Create("workflow.demo"),
    workflow => workflow.DispatchWork("dispatch", childDefinition),
    authorize: auth => auth
        .AllowReadToGroups("workflow.read")
        .AllowOperateToGroups("workflow.ops"));
```

Workflow execution enforces the operation grant, not only the coarse Operate audience metadata. Starting a new run requires `Queue`; resume requires `Start`; pause requires `Pause`; and cancel requires `Cancel`. `AllowOperate...` grants the full set, while the finer-grained builder methods can separate those audiences. Queue and worker-action requirement callbacks apply to workflows as well, using the proposed or retained workflow input respectively.

Read remains independent from workflow execution. Both Read and operation grants imply discovery, but a caller does not need Read merely to perform an allowed workflow operation. Hidden workflow names and run ids use the same not-found outcome as nonexistent targets; discoverable targets can still return unauthorized for a missing operation grant.

Operation permission does not imply permission to read retained state. Start and action results return the run id, command/action status, and safe operation messages to an operate-only caller, but omit the workflow-run snapshot. Worker actions, queue completions, and definition reconfiguration follow the same rule: authoritative `Worker` or `Definition` snapshots require Read, and queue completion `Output`/`RawOutput` also require Read, while definition reconfiguration still returns the current `Revision`. Raw unhandled-exception text and metadata require Read or diagnostics access. Operator projections compute `AvailableActions` by intersecting lifecycle-valid actions with the caller's exact `Start`, `Pause`, and `Cancel` grants; a Read-only view therefore does not advertise controls the caller cannot execute.

Declared workflow child dispatch uses the workflow's accepted execution authority rather than the initiating actor's direct permission on each child definition. This does not grant the actor permission to queue those child definitions outside the workflow.

Workflow pause, resume, and cancel use the same containment boundary when they propagate to the run's own outstanding child workers. Performing the equivalent worker action directly still requires permission on the child definition.

Workflow runs store actor, origin, and authentication state from the start request context.

Stored workflow-run request contexts do not retain precomputed authorization snapshots.

Child work is correlated back to the workflow through added identifiers.

Workflow-started child work stores actor, origin, and authentication state from the workflow run request context.

Stored child-worker request contexts do not retain precomputed authorization snapshots.

If code later creates a new `IWorkSystemSession` from a stored workflow-run or worker `WorkRequestContext`, Workable asynchronously resolves groups for the retained actor through the configured `IWorkAuthorizationGroupProvider` when no authorization snapshot is present. This path does not require an active HTTP request.

### Discover, Read, And Operate Rules

Discover permission affects:

- the redacted `IWorkSystemSession.Discovery` definition catalog
- definition names, descriptions, categories, input/output schemas, and tool-oriented metadata
- MCP work-tool descriptor projection

Read and operate access both imply discovery. Explicit discovery does not imply either read or operate access.

Read permission affects:

- catalog definition and schema listing
- work-definition queries
- worker and iteration queries
- work-key and iteration-key queries
- event subscriptions
- HTTP and SignalR views built from those reads

Operate permission affects:

- queueing work
- worker actions
- worker reconfiguration
- definition reconfiguration

Use `AllowReadToGroups(...)` and/or `AllowReadToKnownAuthenticatedUsers()` to configure the read audience independently of those operate permissions.

Use `AllowDiscoverToGroups(...)` and/or `AllowDiscoverToKnownAuthenticatedUsers()` when a caller needs only redacted definition and schema discovery.

`AllowOperateToGroups(...)` and `AllowOperateToKnownAuthenticatedUsers()` are the easy full-surface grants. The finer-grained helpers participate in that same overall operate surface:

- `AllowQueueToGroups(...)` and `AllowQueueToKnownAuthenticatedUsers()`
- `AllowWorkerActionsToGroups(...)` and `AllowWorkerActionsToKnownAuthenticatedUsers()`
- `AllowOperationsToGroups(...)` and `AllowOperationsToKnownAuthenticatedUsers(...)`

Those finer-grained grants still aggregate into the definition's coarse operate metadata. That means the catalog and access-summary surfaces continue to answer the broad question "can this caller operate this definition at all," while the runtime queue/action/reconfiguration paths enforce the specific operation being attempted.

### Constrained Operate Grants

Constrained operate grants add an optional second-level authorization check on top of the normal work-level operate audience.

This is useful when the work itself is intentionally generic, but the caller's authority depends on request input.

Typical examples are:

- one survey-administration work definition that can operate on many surveys, where a broad `survey.admin` group can operate everything
- a smaller ownership group that should only operate surveys for one area such as `north`
- one server-definition editor work that can edit many server definitions, where some groups should only edit a subset identified by input

Without constrained operate grants, the usual alternative is to create duplicate work definitions for each secured audience or area. That works technically, but it pushes security partitioning into your work catalog:

- the executor logic is duplicated or artificially split
- schemas, docs, and tooling projections drift across near-identical definitions
- metrics, history, and operational views become fragmented across several names for the same logical operation

Constrained operate grants let you keep one definition for one logical operation, then discriminate queueing, worker actions, or reconfiguration by input such as `AreaKey`, `ServerKey`, or another business identifier.

- `WhenOperatingRequire(...)`
  - applies to queueing, worker actions, worker reconfiguration, and definition reconfiguration
- `WhenQueueingRequire(...)`
  - applies only to queueing
- `WhenWorkerActionsRequire(...)`
  - applies only to worker actions
- `WhenReconfiguringRequire(...)`
  - applies to both worker and definition reconfiguration
- `WhenWorkerReconfiguringRequire(...)`
  - applies only to worker reconfiguration
- `WhenDefinitionReconfiguringRequire(...)`
  - applies only to definition reconfiguration

Important details:

- requirement delegates are synchronous and return `bool`
- multiple requirements on one grant are OR'ed and stop after the first `true`
- multiple grant blocks for the same audience are also additive; if any matching grant allows the current operation, the operation is authorized
- typed queue requirements deserialize from the incoming queued input
- typed worker-action and worker-reconfiguration requirements deserialize from the worker's persisted original input
- definition reconfiguration has no work input of its own, so definition-reconfiguration-specific requirements inspect the reconfiguration change shape instead
- deserialize failures fail closed and return an invalid outcome
- this extra layer does not change read visibility

For example, a host can keep one survey editor definition and layer broad and narrow operate grants together:

```csharp
builder.AddWork<AdminSurveyWork>(
    authorize: auth => auth
        .AllowOperateToGroups("survey.admin")
        .AllowOperateToGroups(
            ["survey.north.owner"],
            operate => operate.WhenOperatingRequire<AdminSurveyArgs>(context =>
                context.Input?.AreaKey == "north")));
```

In that shape:

- `survey.admin` can queue and control the work for any survey area
- `survey.north.owner` can use the same work definition, but only when the input targets `north`
- the system does not need separate definitions such as `admin-survey-north`, `admin-survey-south`, and so on

System-wide broad operate grants remain unconditional:

- `AllowOperateAllWorkToGroups(...)`
- `WorkAdministrators(...)`

If a constrained work-level operate group is also covered by either broad system-level grant, Workable logs a warning because that work-level constraint can never restrict callers in that group.

With authorization enabled:

- if a caller cannot read a work definition, it is filtered out
- if a caller can read but cannot operate, queue and worker operations return unauthorized outcomes

## System Authorization

System authorization is configured on the host, not on individual work definitions.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.ConfigureAuthorization(auth => auth
        .SystemAdministrators("workable.sysadmin")
        .WorkAdministrators("workable.workadmin")
        .AllowBuiltInHttpApiToGroups("workable.surface-user")
        .AllowDiagnosticsToGroups("workable.diagnostics")
        .AllowControlSystemToGroups("workable.control")
        .AllowReadAllWorkToGroups("support.readall")
        .AllowOperateAllWorkToGroups("ops.operateall"));
});
```

Built-in role semantics are:

- `SystemAdministrators(...)`
  - grants `Diagnostics`
  - grants `ControlSystem`
  - grants `ReadAllWork`
  - does not grant queueing, worker operations, or `OperateAllWork`
- `WorkAdministrators(...)`
  - grants `ReadAllWork`
  - grants `OperateAllWork`

This separation is intentional. A system administrator can inspect diagnostics, manage lifecycle, and create a temporary full-profile capture rule, but another caller still needs queue permission for the matching work definition. Creating a capture rule never elevates the creator's work-operation permissions.

When the host maps the built-in `Workable.HttpApi` routes through `MapWorkableApi(...)`, both roles also grant entry to that built-in `/workable` surface for the same system. Host-defined endpoints that call Workable directly are unaffected.

### Built-In HTTP API Gates

`MapWorkableApi(...)` applies authorization in layers:

1. transport authentication
2. optional outer gate through `WorkableHttpApiOptions.SurfaceAccessGroups`
3. required inner built-in surface gate for the target system
4. normal session-level system and work-definition authorization

The outer gate is host-wide for the built-in `/workable` surface. It does not ask which system the caller is targeting. It simply answers "may this caller enter the built-in Workable HTTP surface at all?"

The inner gate is system-scoped. It allows callers who are `SystemAdministrator`, `WorkAdministrator`, or members of groups configured through `AllowBuiltInHttpApiToGroups(...)` for that specific system.

That distinction is deliberate:

- outer gate is for locking down the existence of the built-in `/workable` surface
- inner gate is for deciding which systems may be used through that built-in surface
- host-defined endpoints that call Workable directly do not pass through either gate unless the host intentionally reuses the same policy

Once `WorkableHttpApiOptions.SurfaceAccessGroups` contains at least one group, every caller to every built-in `/workable` route must satisfy that outer gate. Configuring one outer-gate group does not "turn surface access on for everyone"; it turns the host-wide outer check on for everyone.

`/workable/host` also uses the inner gate. It returns only systems where the caller has both:

- built-in surface access for that system
- some actual Workable access inside that system

Named built-in routes such as `/workable/systems/{systemName}/...` also require both built-in surface access and actual system access. The built-in surface gate is checked first, then the normal system access rules apply inside the selected system. A named system that fails either check uses the same not-found response as an unknown name so the route cannot enumerate registered systems.

Granular system permissions are:

- `AllowDiagnosticsToGroups(...)`
  - controls `IWorkSystemSession.Diagnostics`, transport diagnostics routes/views, and listing temporary profiling capture rules
  - controls whether profile data is present in authorized worker queries, iteration queries, queue completions, worker-action outcomes, and system-stop results
  - is also required when an authorized queue request or definition reconfiguration explicitly selects full profile capture
- `AllowControlSystemToGroups(...)`
  - controls starting and stopping the system lifecycle
  - controls creating or deleting temporary profiling and persistent execution-diagnostics capture rules
- `AllowBuiltInHttpApiToGroups(...)`
  - grants access to the built-in `MapWorkableApi(...)` HTTP surface for that system without also granting administrator semantics
- `AllowReadAllWorkToGroups(...)`
  - grants read access to every work definition without stamping each definition individually
- `AllowOperateAllWorkToGroups(...)`
  - grants operate access to every work definition without stamping each definition individually

### Inspect Access

Hosts can inspect system access explicitly through `IWorkSystem`.

- `DescribeAccess(requestContext)` returns a `WorkSystemAccessSummary` with the caller's current system-level access.

`WorkSystemAccessSummary` reports:

- `IsSystemAdministrator`
- `IsWorkAdministrator`
- `CanViewDiagnostics`
- `CanControlSystem`
  - whether the caller can start or stop the system lifecycle
- `CanReadAllWork`
- `CanOperateAllWork`
- `CanDiscoverAllWork`
- total, discoverable, readable, and operable work-definition counts
- readable and operable workflow-definition counts

`(await DescribeAccess(...)).HasAnyAccess()` answers whether the caller has enough real access for the system to appear in transport discovery or to be selected by name through transport adapters. An empty catalog does not imply all-definition discovery, while legitimate workflow-only access still qualifies through the workflow counts.

The in-process `WorkSystemAccessSummary.TotalDefinitionCount` is the complete registered count. Transport adapters may redact that value for callers without `CanDiscoverAllWork`; the built-in HTTP host descriptor reports the discoverable count in its `totalDefinitionCount` field so it does not reveal how many hidden definitions exist.

This is especially useful for custom UIs, capability negotiation, or host-specific feature gating before a caller attempts the broader session surface.

When authorization is required, failures in this area can surface as:

- `WorkSystemAuthorizationRequiredException`
- `WorkSystemAccessDeniedException`

These are different from `WorkQueueOutcome.Unauthorized` or `WorkActionOutcome.Unauthorized`, which apply to one definition or worker rather than system-level access.

## Microsoft Entra Target Apps

Use `Workable.Entra` when the hosted application already authenticates Microsoft Entra identities and Workable-facing surfaces should interpret their actor and group claims.

See [Microsoft Entra Authentication](../guides/entra-authentication.md) for the dedicated setup guide and option reference.

In Workable terms, Entra authentication remains owned by the host. `Workable.Entra` maps selected claims from the authenticated principal into Workable groups, and then Workable evaluates its normal system and work authorization rules against those group values.

ASP.NET Core endpoint authorization and Workable authorization are separate gates. A host default, named, or fallback policy can reject a request before a Workable adapter runs; after the request reaches Workable, system and work rules still decide what the selected actor and groups may do. Selecting an existing authentication scheme for Workable changes its actor/group principal but does not bypass a host endpoint policy or grant Workable permissions.

## How It Applies

### Session Composition

Internally, Workable creates session-bound services for:

- catalog
- queue
- worker operations
- query
- event stream
- diagnostics

When authorization is enabled, Workable wraps those session services in authorization decorators:

- `AuthorizedWorkCatalog`
- `AuthorizedWorkQueueService`
- `AuthorizedWorkerOperations`
- `AuthorizedWorkQueryService`
- `AuthorizedWorkEventStream`
- `UnauthorizedWorkSystemDiagnostics` when the caller cannot view diagnostics

That session composition is why the caller-scoped surface stays coherent. The same request context drives catalog visibility, query filtering, queue authorization, worker-control authorization, event visibility, and diagnostics access together instead of each surface making an independent guess.

Authorization data comes from either:

- `WorkRequestContext.Authorization`, when the caller already has a trusted authorization snapshot scoped to the target system
- `IWorkAuthorizationGroupProvider.GetGroups(...)`, when groups should be resolved from the retained actor
- an adapter-provided invocation context, such as matching claims from the current HTTP user, when one applies

Create trusted snapshots with `WorkAuthorizationSnapshot.CreateForSystem(...)`. The logical system-name scope survives host restarts; an explicit `null` system name identifies the default unnamed system. When session creation receives an unscoped snapshot, a snapshot for another system, or a snapshot for another actor, Workable removes it, resolves authorization through the normal context-provider and actor-provider fallback, and captures a replacement snapshot scoped to the current system for the returned session. Worker and workflow persistence continues to remove precomputed authorization snapshots from stored request contexts.

Authorization snapshots are trusted in-process data, not credentials or anti-forgery tokens. Built-in HTTP, MCP, and SignalR transports do not bind snapshots or caller-supplied groups from wire payloads. The logical system scope prevents accidental reuse between configured systems; code that can directly construct a scoped snapshot and choose its groups already runs inside the application's trusted boundary.

For the built-in `Workable.HttpApi` adapter, those group and access resolutions are coordinated through a request-scoped cache instead of each step resolving independently. The adapter reuses that cached state across the outer gate, inner gate, host discovery, named-system selection, and request-context creation for the selected system.

That cache is intentionally request-scoped and assumes normal sequential pipeline use. It should not be treated as safe for parallel mutation by multiple concurrent authorization tasks inside one HTTP request without adding synchronization.

The request context can also carry `IsAuthenticated`. Workable uses that together with the resolved actor to evaluate known-authenticated-user discover, read, and operate grants, including `AllowDiscoverToKnownAuthenticatedUsers()`, `AllowReadToKnownAuthenticatedUsers()`, and `AllowOperateToKnownAuthenticatedUsers()`. Canonical authorization snapshots retain this authentication state and include it in their read-visibility fingerprint so delayed projection work cannot elevate or discard it while replaying the snapshot.

SignalR needs one extra step because broadcasts happen after the original request is gone. On subscribe:

- the hub resolves groups once
- Workable computes the caller's readable definition set
- Workable stores a `WorkAuthorizationSnapshot` on the realtime subscription

The broadcaster later recreates a session from that snapshot, preserving the original authentication state, and shared realtime groups are keyed by a read-visibility fingerprint instead of the caller's raw group list. That lets callers share broadcasts only when they can see the same work.
