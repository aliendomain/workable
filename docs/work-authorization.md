# Work Authorization

Workable supports authorization at two levels:

- work-definition authorization controls who can read or operate individual work definitions
- system authorization controls who can discover a system, view diagnostics, or start and stop it

The model is request-context based. Callers create or receive a `WorkRequestContext`, Workable creates an `IWorkSystemSession`, and that session exposes the caller-scoped catalog, queue, worker operations, query service, event stream, and diagnostics.

## Security Model

When `RequireAuthorization(true)` is enabled on a system:

- work with no authorization configured is closed by default
- read surfaces filter out work the caller cannot read
- queueing and worker operations return unauthorized outcomes when the caller cannot operate the target work
- diagnostics require system-level diagnostics permission
- start and stop require system-level control permission
- system discovery is filtered by system-level connect permission

When `RequireAuthorization(false)` is enabled:

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
    builder.RequireAuthorization();
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

## ASP.NET Core Integration

Use `Workable.AspNetCore` when you need to create request contexts from `HttpContext` in your own endpoints or custom transports.

```csharp
builder.Services.AddWorkableAspNetCoreAuthorization();
```

This registers:

- `IWorkActorFactory`
- `IWorkRequestContextFactory`
- a default `IWorkAuthorizationGroupProvider`

The default group provider reads group claims from `HttpContext.User`. By default it looks at:

- `groups`
- `roles`
- `role`
- `ClaimTypes.Role`

Actor id, name, and email claim mappings are also configurable through `WorkableAspNetCoreAuthorizationOptions`.

```csharp
builder.Services.AddWorkableAspNetCoreAuthorization(options =>
{
    options.ActorIdClaimTypes = ["sub", ClaimTypes.NameIdentifier];
    options.GroupClaimTypes = ["groups", "roles"];
});
```

If the host already has a custom group resolver, register `IWorkAuthorizationGroupProvider` yourself before calling the transport package setup or as your final override.

### Custom Endpoint Example

For custom ASP.NET Core endpoints, create a request context explicitly and queue through a session.

```csharp
app.MapPost("/internal/work/welcome/{userId}", async (
    string userId,
    HttpContext httpContext,
    IWorkSystem system,
    IWorkRequestContextFactory requestContexts,
    CancellationToken cancellationToken) =>
{
    var requestContext = requestContexts.Create(
        httpContext,
        WorkInvocationChannel.HttpApi,
        "Queue welcome email from custom endpoint.");

    var session = system.CreateSession(requestContext);
    return await session.Queue.Enqueue(
        "email.welcome.send",
        new SendWelcomeEmailArgs(userId),
        cancellationToken: cancellationToken);
});
```

Transport packages already do this for you:

- `AddWorkableHttpApi` and `MapWorkableApi`
- `AddWorkableMcpServer` and `MapWorkableMcp`
- `AddWorkableSignalR` and `MapWorkableSignalR`

## Under The Hood

### Request Contexts And Sessions

`WorkRequestContext` carries:

- the `WorkActor`
- the `WorkOrigin`
- an optional pre-resolved `WorkAuthorizationSnapshot`

`IWorkSystem.CreateSession(...)` binds that request context once and returns a caller-scoped `IWorkSystemSession`.

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

The session factory resolves groups from either:

- `WorkRequestContext.Authorization`, when the caller already has a trusted authorization snapshot
- `IWorkAuthorizationGroupProvider`, when groups should be resolved for the current request

### HTTP, MCP, And SignalR

The ASP.NET Core adapters all build `WorkRequestContext` values from the incoming request and reject anonymous callers before adapter handlers process request bodies or hub/tool methods.

- `HTTP` uses `WorkInvocationChannel.HttpApi`
- `MCP` uses `WorkInvocationChannel.Mcp`
- `SignalR` uses `WorkInvocationChannel.SignalR`

SignalR needs one extra step because broadcasts happen after the original request is gone. On subscribe:

- the hub resolves groups once
- Workable computes the caller's readable definition set
- Workable stores a `WorkAuthorizationSnapshot` on the realtime subscription

The broadcaster later recreates a session from that snapshot, and shared realtime groups are keyed by a read-visibility fingerprint instead of the caller's raw group list. That lets callers share broadcasts only when they can see the same work.

### Direct .NET Calls

Direct `.NET` calls still work without ASP.NET Core:

- `IWorkSystem.Queue` and `IWorkSystem.Workers` use `WorkInvocationChannel.DotNet`
- the default actor is `WorkActor.Unknown`

When a direct caller needs actor-aware or authorization-aware behavior, it should create a `WorkRequestContext` and use `IWorkSystem.CreateSession(...)` explicitly.
