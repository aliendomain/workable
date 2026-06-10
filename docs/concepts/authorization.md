# Work Authorization

Workable supports authorization at two levels:

- work-definition authorization controls who can read or operate individual work definitions
- system authorization controls who can discover a system when they have actual access to it, view diagnostics, or start and stop it

The model is request-context based. Callers create or receive a `WorkRequestContext`, Workable creates an `IWorkSystemSession`, and that session exposes the caller-scoped catalog, queue, worker operations, query service, event stream, and diagnostics.

## Security Model

Systems are authorization-enabled by default. Most hosts do not need to call `RequireAuthorization()` explicitly unless they want the code to say so.

When authorization is enabled on a system:

- work with no authorization configured is closed by default
- read surfaces filter out work the caller cannot read
- queueing and worker operations return unauthorized outcomes when the caller cannot operate the target work
- diagnostics require system-level diagnostics permission
- start and stop require system-level control permission
- system discovery is filtered to systems where the caller has actual access

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
- whether operate access is also allowed to known authenticated users
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

Or allow queueing and worker operations for callers that are both authenticated and resolved to a known `WorkActor`:

```csharp
builder.AddWork<SyncInvoicesWork>(
    configure: null,
    authorize: auth => auth.AllowOperateToKnownAuthenticatedUsers());
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

This capability is currently available through the fluent builder API, not through `WorkAuthorizationAttribute`.

This rule is intentionally narrower than "authenticated transport request." The caller must be authenticated and the request context must carry a known actor with at least one non-blank identity field such as `Id`, `Name`, or `Email`.

For ASP.NET Core transports and custom endpoints that use `IWorkRequestContextFactory`, Workable sets this automatically from `HttpContext`. For trusted direct in-process callers that build `WorkRequestContext` values manually, the caller is responsible for setting `isAuthenticated: true` when that meaning is intended.

Fluent authorization overrides attribute authorization.

Within `WithWorkDefaults(...)`, the group-level `authorize` callback runs before any per-work `authorize` callback. That means a specific work can refine or replace the grouped authorization without repeating the shared policy for every sibling registration.

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

`AllowOperateToKnownAuthenticatedUsers()` participates in the same operate surface. It is an alternative operate grant, not a separate permission kind.

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
- `WorkAdministrators(...)`
  - grants `ReadAllWork`
  - grants `OperateAllWork`

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

Named built-in routes such as `/workable/systems/{systemName}/...` also require both built-in surface access and actual system access. The built-in surface gate is checked first, then the normal system access rules apply inside the selected system.

Granular system permissions are:

- `AllowDiagnosticsToGroups(...)`
  - controls `IWorkSystemSession.Diagnostics` and transport diagnostics routes/views
- `AllowControlSystemToGroups(...)`
  - controls start and stop
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
- `CanReadAllWork`
- `CanOperateAllWork`
- total, readable, and operable definition counts

`DescribeAccess(...).HasAnyAccess()` answers whether the caller has enough real access for the system to appear in transport discovery or to be selected by name through transport adapters.

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

For the built-in `Workable.HttpApi` adapter, those group and access resolutions are coordinated through a request-scoped cache instead of each step resolving independently. The adapter reuses that cached state across the outer gate, inner gate, host discovery, named-system selection, and request-context creation for the selected system.

That cache is intentionally request-scoped and assumes normal sequential pipeline use. It should not be treated as safe for parallel mutation by multiple concurrent authorization tasks inside one HTTP request without adding synchronization.

The request context can also carry `IsAuthenticated`. Workable uses that together with the resolved actor to evaluate `AllowOperateToKnownAuthenticatedUsers()`.

SignalR needs one extra step because broadcasts happen after the original request is gone. On subscribe:

- the hub resolves groups once
- Workable computes the caller's readable definition set
- Workable stores a `WorkAuthorizationSnapshot` on the realtime subscription

The broadcaster later recreates a session from that snapshot, and shared realtime groups are keyed by a read-visibility fingerprint instead of the caller's raw group list. That lets callers share broadcasts only when they can see the same work.
