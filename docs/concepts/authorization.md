# Work Authorization

Workable supports authorization at two levels:

- work-definition authorization controls who can read or operate individual work definitions
- system authorization controls who can discover a system, view diagnostics, or start and stop it

The model is request-context based. Callers create or receive a `WorkRequestContext`, Workable creates an `IWorkSystemSession`, and that session exposes the caller-scoped catalog, queue, worker operations, query service, event stream, and diagnostics.

## Security Model

Systems are authorization-enabled by default. Most hosts do not need to call `RequireAuthorization()` explicitly unless they want the code to say so.

When authorization is enabled on a system:

- work with no authorization configured is closed by default
- read surfaces filter out work the caller cannot read
- queueing and worker operations return unauthorized outcomes when the caller cannot operate the target work
- diagnostics require system-level diagnostics permission
- start and stop require system-level control permission
- system discovery is filtered by system-level connect permission

Turn authorization off only when the system is intentionally open to all callers:

```csharp
services.AddWorkableSystem(builder =>
{
    builder.RequireAuthorization(false);
});
```

This opt-out only applies to direct in-process use of the core runtime. The current transport adapters still require authorization-enabled systems, and their mapping methods throw when that precondition is not met. See the transport adapter docs for the exact mapping behavior and constraints.

When `RequireAuthorization(false)` is set:

- work-definition and system-level authorization are not enforced
- direct `.NET` callers can still use `WorkRequestContext` and `IWorkSystemSession`
- authorization metadata remains on the catalog as design-time information

Current adapter behavior is intentionally stricter than the core runtime:

- `Workable.HttpApi` requires authenticated callers and authorization-enabled systems
- `Workable.Mcp` requires authenticated callers and authorization-enabled systems
- `Workable.SignalR` requires authenticated callers and authorization-enabled systems

## Work Authorization

Each `WorkDefinition` carries non-null authorization metadata:

- read groups
- operate groups
- the source of each permission set: `None`, `Attribute`, or `Fluent`

That metadata is visible through catalog and definition queries so callers can inspect what a work definition requires.

### Attribute-Based Authorization

```csharp
[WorkMetadata("billing.invoice.sync", "Billing")]
[WorkAuthorization(
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

You can also configure the two surfaces independently:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth
        .AllowReadToGroups("billing.read", "billing.admin")
        .AllowOperateToGroups("billing.ops", "billing.admin"));
```

Fluent authorization overrides attribute authorization.

### Read And Operate Rules

Read permission affects:

- catalog definition listing
- work-definition queries
- worker and iteration queries
- work-key and iteration-key queries
- event subscriptions
- HTTP and SignalR views built from those reads

Operate permission affects:

- queueing work
- worker actions
- worker reconfiguration

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
        .AllowConnectToGroups("workable.connect")
        .AllowDiagnosticsToGroups("workable.diagnostics")
        .AllowControlSystemToGroups("workable.control")
        .AllowReadAllWorkToGroups("support.readall")
        .AllowOperateAllWorkToGroups("ops.operateall"));
});
```

Built-in role semantics are:

- `SystemAdministrators(...)`
  - grants `Connect`
  - grants `Diagnostics`
  - grants `ControlSystem`
  - grants `ReadAllWork`
- `WorkAdministrators(...)`
  - grants `ReadAllWork`
  - grants `OperateAllWork`

Granular system permissions are:

- `AllowConnectToGroups(...)`
  - controls whether a caller can discover the system in transport-level system lists
- `AllowDiagnosticsToGroups(...)`
  - controls `IWorkSystemSession.Diagnostics` and transport diagnostics routes/views
- `AllowControlSystemToGroups(...)`
  - controls start and stop
- `AllowReadAllWorkToGroups(...)`
  - grants read access to every work definition without stamping each definition individually
- `AllowOperateAllWorkToGroups(...)`
  - grants operate access to every work definition without stamping each definition individually

### Inspect Access

Hosts can inspect system access explicitly through `IWorkSystem`.

- `CanConnect(requestContext)` answers whether the caller can discover the system through transport-facing surfaces.
- `DescribeAccess(requestContext)` returns a `WorkSystemAccessSummary` with the caller's current system-level access.

`WorkSystemAccessSummary` reports:

- `CanConnect`
- `IsSystemAdministrator`
- `IsWorkAdministrator`
- `CanViewDiagnostics`
- `CanControlSystem`
- `CanReadAllWork`
- `CanOperateAllWork`
- total, readable, and operable definition counts

This is especially useful for custom UIs, capability negotiation, or host-specific feature gating before a caller attempts the broader session surface.

When authorization is required, failures in this area can surface as:

- `WorkSystemAuthorizationRequiredException`
- `WorkSystemAccessDeniedException`

These are different from `WorkQueueOutcome.Unauthorized` or `WorkActionOutcome.Unauthorized`, which apply to one definition or worker rather than system-level access.

## Microsoft Entra Target Apps

Use `Workable.Entra` when the hosted application should accept Microsoft Entra ID bearer tokens for Workable-facing surfaces.

See [Microsoft Entra Authentication](../guides/entra-authentication.md) for the dedicated setup guide and option reference.

In Workable terms, Entra is an authentication and group-mapping strategy, not a separate authorization model. It validates bearer tokens, maps selected Entra claims into Workable groups, and then Workable evaluates its normal system and work authorization rules against those group values.

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

- `WorkRequestContext.Authorization`, when the caller already has a trusted authorization snapshot
- `IWorkAuthorizationGroupProvider`, when groups should be resolved for the current request

SignalR needs one extra step because broadcasts happen after the original request is gone. On subscribe:

- the hub resolves groups once
- Workable computes the caller's readable definition set
- Workable stores a `WorkAuthorizationSnapshot` on the realtime subscription

The broadcaster later recreates a session from that snapshot, and shared realtime groups are keyed by a read-visibility fingerprint instead of the caller's raw group list. That lets callers share broadcasts only when they can see the same work.
