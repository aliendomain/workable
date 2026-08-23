# ASP.NET Core Integration

`Workable.AspNetCore` is mostly a transitive package.

Most applications pick it up indirectly through `Workable.HttpApi`, `Workable.Mcp`, or `Workable.SignalR`. Reference it directly when you are building your own ASP.NET Core endpoints or your own transport and need to turn the current `HttpContext` into a Workable request context.

This package does not add Workable routes. It does not choose your authentication strategy. Its job is narrower: take the authenticated ASP.NET Core request you already have and translate it into Workable's actor, origin, authenticated-caller signal, and authorization-group model.

## When To Use It

Use `Workable.AspNetCore` directly when:

- your application has custom controllers or minimal APIs that queue work through `IHttpContextWorkCommandDispatcher`
- your application has custom controllers or minimal APIs that start or operate workflow runs through `IHttpContextWorkflowCommandDispatcher`
- you are building your own ASP.NET Core transport instead of using Workable's built-in HTTP, MCP, or SignalR adapters
- you want Workable actor and authorization-group resolution to come from `HttpContext.User`

Do not add it just to host Workable. If you only use the built-in adapters, they already register it for you.

## What It Registers

Call `AddWorkableAspNetCoreAuthorization` to register the ASP.NET Core integration services:

```csharp
builder.Services.AddWorkableAspNetCoreAuthorization();
```

That registers:

- request-scoped `IWorkActorFactory`
- request-scoped `IWorkRequestContextFactory`
- request-scoped `IHttpContextWorkCommandDispatcher`
- request-scoped `IHttpContextWorkflowCommandDispatcher`
- a singleton bridge for the HTTP claims-based authorization-group context provider; it resolves scoped selectors and mappers from the active request rather than capturing them at the root
- `IHttpContextAccessor` when one is not already registered

Do not inject the request-scoped services into a singleton. Resolve them from a request or explicit service scope. Host identity selectors and actor/group claim mappers may themselves be scoped.

## Preferred HTTP Queueing Path

For custom ASP.NET Core endpoints that need to queue work, prefer `IHttpContextWorkCommandDispatcher`.

It wraps the common HTTP orchestration path:

- create a `WorkRequestContext` from the current `HttpContext`
- resolve the current actor, URL, and authenticated-caller signal
- dispatch the request through Workable using a standardized `WorkDispatchResult<T>`

When used from a host-defined endpoint, this path records `WorkInvocationChannel.HttpApi` with `WorkOriginSurface.HostApplication`. Built-in Workable adapter endpoints such as `MapWorkableApi(...)` still use the same `HttpApi` channel, but stamp `WorkOriginSurface.WorkableAdapter` so the origin stays distinguishable in worker history and query payloads.

```csharp
app.MapPost("/welcome/{userId}", async (
    string userId,
    IHttpContextWorkCommandDispatcher commands,
    CancellationToken cancellationToken) =>
{
    var result = await commands.Dispatch<SendWelcomeEmailArgs, object?>(
        "email.welcome.send",
        new SendWelcomeEmailArgs(userId),
        "Queue welcome email from custom endpoint.",
        new WorkDispatchOptions(WorkDispatchCompletion.ReturnAfterAccepted),
        cancellationToken);

    return Results.Ok(new
    {
        result.Status,
        result.WorkerId,
        result.ErrorCode,
        result.ErrorMessage,
    });
});
```

When `TransportAuthenticationScheme` selects an existing host scheme, the work and workflow HTTP-context
dispatchers authenticate that scheme before creating their request context. They keep the resulting principal private
to Workable and do not replace `HttpContext.User`, so custom endpoints get the same actor and group behavior as the
built-in HTTP, MCP, and SignalR adapters without a Workable-specific preauthentication step. The dispatchers freeze the
selected identity, actor, and claims-derived groups once for the operation rather than rerunning host selectors or
mappers later in the call.

Use `WorkDispatchCompletion.WaitForCompletion` when the caller needs a terminal result in the HTTP response instead of returning after acceptance. The final output is included only when the caller has Read permission for that definition.

## Preferred HTTP Workflow Command Path

For custom ASP.NET Core endpoints that need to start or operate workflows, prefer `IHttpContextWorkflowCommandDispatcher`.

```csharp
app.MapPost("/orders/{orderId}/fulfill", async (
    string orderId,
    IHttpContextWorkflowCommandDispatcher workflows,
    CancellationToken cancellationToken) =>
{
    var result = await workflows.Start(
        "orders.fulfillment",
        $"Start fulfillment for order {orderId}.",
        new WorkflowCommandOptions(WorkDispatchCompletion.ReturnAfterAccepted),
        cancellationToken);

    return Results.Ok(new
    {
        result.Status,
        result.RunId,
        result.RunStatus,
        result.ErrorCode,
        result.ErrorMessage,
    });
});
```

Use `WorkflowRunAction.Start`, `WorkflowRunAction.Pause`, and `WorkflowRunAction.Cancel` with `Execute(...)` when a custom endpoint needs to operate an existing workflow run.

## Request Context Creation

`IWorkRequestContextFactory` is still the lower-level entry point.

Use it when you need more than queueing, such as creating a session for direct query, worker action, catalog, or lifecycle access, or when you are building a custom transport that does not fit the dispatcher abstraction.

It builds a `WorkRequestContext` from the current `HttpContext`, the intended `WorkInvocationChannel`, and an optional short description of what the request is doing.

This lower-level factory reads the principal already selected for Workable; it does not perform asynchronous
authentication itself. The built-in adapters and both HTTP-context dispatchers initialize an explicit transport scheme
before calling it. A custom endpoint that deliberately uses the factory directly with `TransportAuthenticationScheme`
must first call `await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext)`. No extra call is needed
in ambient-principal mode.

`EnsureAuthenticatedAsync` returns `false` when the selected scheme does not authenticate. A custom endpoint must stop at that point and apply its own host-defined failure behavior; creating a request context after `false` produces an unauthenticated Workable context. The higher-level HTTP-context dispatchers perform the initialization automatically but still return normal Workable authorization outcomes rather than defining the endpoint's ASP.NET Core policy.

```csharp
app.MapPost("/welcome/{userId}", async (
    string userId,
    HttpContext httpContext,
    IWorkSystem system,
    IWorkRequestContextFactory requestContexts,
    CancellationToken cancellationToken) =>
{
    if (!await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
    {
        return Results.Unauthorized(); // The host can choose a policy/challenge response instead.
    }

    var requestContext = requestContexts.Create(
        httpContext,
        WorkInvocationChannel.HttpApi,
        "Queue welcome email from custom endpoint.");

    var session = await system.CreateSession(requestContext, cancellationToken);

    var outcome = await session.Queue.Enqueue(
        "email.welcome.send",
        new SendWelcomeEmailArgs(userId),
        cancellationToken: cancellationToken);

    return Results.Ok(outcome);
});
```

The created context includes:

- a `WorkActor` derived from the current authenticated user
- a `WorkOrigin` that records the invocation channel and request URL
- a `WorkOriginSurface` value that defaults to `HostApplication` for host-defined endpoints
- `IsAuthenticated`, derived from the current ASP.NET Core principal
- any authorization group resolution performed by Workable later through the registered group provider

When `TransportAuthenticationScheme` is configured, the public synchronous `IsAuthenticated(httpContext)` helper reports only an already-selected Workable principal; it does not perform scheme authentication. Call the asynchronous `EnsureAuthenticatedAsync(...)` or `GetAuthenticatedPrincipalAsync(...)` first when the explicit scheme has not yet been evaluated.

The `description` argument is optional. Supply it only when the endpoint has useful human-readable context worth preserving on the Workable origin.

The default request-context factory records only `PathBase` and `Path` for HTTP, MCP, and SignalR requests. Query values can contain bearer credentials, authorization codes, transport identifiers, or other caller-controlled secrets, so Workable never copies them into request provenance.

That authenticated-caller signal is what lets work definitions use `AllowDiscoverToKnownAuthenticatedUsers()`, `AllowReadToKnownAuthenticatedUsers()`, `AllowOperateToKnownAuthenticatedUsers()`, `AllowQueueToKnownAuthenticatedUsers()`, or `AllowOperationsToKnownAuthenticatedUsers(...)` without inventing a synthetic authorization group.

## Actor Resolution

`IWorkActorFactory` is responsible for turning the current user into a `WorkActor`.

By default:

- actor id comes from the first matching claim in `ActorIdClaimTypes`
- actor name comes from the selected identity's `Name`, then the configured `ActorNameClaimTypes`
- actor email comes from the configured `ActorEmailClaimTypes`
- anonymous users become `WorkActor.Unknown`

This is usually enough for custom endpoints that already have authenticated users and just need Workable to preserve that identity in queue origins, action history, and authorization evaluation. It also means a caller only qualifies for known-authenticated-user work grants when ASP.NET Core authentication succeeds and Workable can resolve a known actor.

## Authorization Group Resolution

The ASP.NET Core integration selects exactly one authenticated identity from the principal Workable receives. Actor id, name, email, authentication state, and authorization groups all come from that same identity; claims from secondary identities are not combined. This is normally the primary identity on `HttpContext.User`; when an explicit transport scheme is configured, the principal is the Workable-scoped result of authenticating that scheme. Built-in adapters and HTTP-context dispatchers freeze that selection, its actor projection, and its claims-derived groups in one request snapshot.

Hosts with a composite principal can replace `IWorkClaimsIdentitySelector` to select another authenticated identity. Register the selector before or after `AddWorkableAspNetCoreAuthorization`; Workable's default uses `TryAdd` and does not replace a host registration. Selectors and actor/group claim mappers may be scoped when they depend on other request-scoped host services; Workable resolves each one from the initiating request scope and invokes it once while freezing the snapshot. A selector may return a normalized or cloned authenticated identity; Workable does not require repeated calls to return the same object reference.

For SignalR, the snapshot is completed before the initiating connection request scope ends and is retained as
connection state without retaining the request or its service provider. Hub invocations, deferred stream enumeration,
and later long-poll requests reuse it instead of resolving identity services from a disposed connection request scope
or from the current poll request. This keeps one actor and group set
for the connection without changing the host's ambient `IHttpContextAccessor`.

The ambient connection snapshot is applied only when the default request-context factory receives the same
connection `HttpContext` that owns that snapshot. If host code explicitly supplies a different `HttpContext`, that
argument remains authoritative for actor, authentication, and URL projection even when the call occurs inside a
SignalR invocation.

By default it looks at:

- `groups`
- `roles`
- `role`
- `ClaimTypes.Role`

If a claim contains multiple group values in one string, the provider can also split that value using the configured separators.

This means Workable's system and work authorization rules can run against the same claims your ASP.NET Core application already trusts.

## Option Surface

`WorkableAspNetCoreAuthorizationOptions` lets you adjust the claim mapping behavior:

- `TransportAuthenticationScheme`: explicitly authenticate Workable transport requests with one scheme instead of trusting the ambient `HttpContext.User`
- `ActorIdClaimTypes`: ordered claim types to probe for actor id
- `ActorNameClaimTypes`: ordered claim types to probe for actor name
- `ActorEmailClaimTypes`: ordered claim types to probe for actor email
- `GroupClaimTypes`: claim types that contribute Workable authorization groups
- `GroupClaimValueSeparators`: separators used when one claim value contains multiple groups
- `GroupClaimValueSeparatorsByClaimType`: claim-specific separators that override the global list

Example:

```csharp
builder.Services.AddWorkableAspNetCoreAuthorization(options =>
{
    options.TransportAuthenticationScheme = "WorkableBearer";
    options.ActorIdClaimTypes = ["sub", ClaimTypes.NameIdentifier];
    options.ActorNameClaimTypes = ["name", ClaimTypes.Name];
    options.ActorEmailClaimTypes = ["email", "preferred_username"];
    options.GroupClaimTypes = ["groups", "roles"];
    options.GroupClaimValueSeparators = [',', ' '];
    options.GroupClaimValueSeparatorsByClaimType["roles"] = [','];
});
```

Claim-specific separators are useful when one claim has a defined wire format that should not affect other authorization values. `Workable.Entra`, for example, splits `scp` on spaces while treating concrete Entra role and group claims as atomic values. A role such as `Billing Admin` or `Region,West` remains intact unless the host explicitly configures a separator for that claim type or handles it with an earlier mapper.

Integration packages can contribute `IWorkActorClaimsMapper` and `IWorkAuthorizationGroupClaimMapper` implementations for identity-specific actor and group semantics. Claims no mapper handles continue through the host's actor claim lists, `GroupClaimTypes`, and separator settings. A group mapper may also handle a claim with an empty result to prevent an explicitly disabled integration mapping from falling through to generic defaults. Host mappers use the default order `0`, while integration fallback mappers use later orders, so host interpretation wins regardless of service-registration order. `Workable.Entra` uses these extension points and does not add or remove entries in host-owned actor or group claim collections.

The default actor factory, request-context factory, and custom-endpoint dispatchers are request-scoped. The claims
context provider remains safe for Workable's singleton authorization resolver by freezing actor, selector, and mapper
results from `HttpContext.RequestServices` instead of capturing host extensions at the root. Claims-derived groups are
identity data and are reused across system lookups; system-specific authorization still evaluates that fixed group set
against each system's own rules.

`TransportAuthenticationScheme` matters when the Workable-facing surface should authenticate with a specific scheme even if the host application uses a different default for browser or cookie auth. When it is set, Workable explicitly authenticates that scheme and keeps the resulting principal in Workable's request state for actor and group resolution. It does not replace `HttpContext.User`, so unrelated host components continue to see the host's ambient principal. On HTTP API and MCP authentication failure, Workable invokes that existing scheme's challenge behavior and then leaves the complete response to the host handler; only the absence of a challenge scheme produces Workable's fallback 401. SignalR challenge behavior instead belongs entirely to the host policy selected on the endpoint, while Workable's connection guard still rejects a principal that its explicit transport scheme does not authenticate.

This is especially useful when the host application's ambient scheme is not the scheme Workable should trust. A common example is an application that uses cookies as its default for browser traffic, while Workable HTTP, MCP, or SignalR requests should authenticate against an existing bearer-token or transport-specific scheme. When the host default endpoint policy does not authenticate that scheme, pass a host-owned named policy through the adapter's `authorizationPolicy` parameter so ASP.NET Core can authenticate it before the endpoint executes. A host that deliberately owns the endpoint through its fallback policy can instead pass `useHostFallbackPolicy: true`.

An explicitly selected Workable scheme affects the principal Workable uses; it does not override the host's endpoint authorization. The default policy, a selected named policy, or an explicitly selected fallback-policy mode continues to run through normal ASP.NET Core authorization and must also succeed.

In most hosts, `Configure<WorkableAspNetCoreAuthorizationOptions>(...)` is enough. Use `PostConfigure(...)` when you need the final override after another registration path has already configured the options and you want Workable's transport scheme to win.

That comes up most often in integration tests or platform-style hosts where Workable is already wired correctly by the target application and the test harness is layering one more authentication override on top. In that situation, registration order is effectively fixed, so `PostConfigure(...)` can be the only practical way to replace the final transport scheme.

## Custom Group Providers

If your application already has a richer authorization model, register an actor-based group provider:

```csharp
builder.Services.AddWorkableAspNetCoreAuthorization();
builder.Services.AddSingleton<IWorkAuthorizationGroupProvider, MyGroupProvider>();
```

`MyGroupProvider.GetGroups(actor, systemName, cancellationToken)` can query a database, directory, or remote permission service. Invocation-context providers run first; the actor-based provider is the fallback when none applies, including durable work and workflow rehydration in background services.

Hosts that need to replace claims-derived groups for a live invocation can instead implement `IWorkAuthorizationGroupContextProvider`. Those providers run by ascending `Order`; host implementations default to `0`, while Workable's ASP.NET Core claims provider uses `1000`. The first provider that returns a non-null result owns the group set. Return `null` when the provider does not apply and an empty set when it applies but intentionally resolves no groups. Context providers are singleton services and must not directly capture scoped dependencies.

Use that path for database lookups, tenant-aware role expansion, application-specific permission projection, or any host that uses durability and needs to resolve the queued actor after the original request has ended.

## Relationship To Workable Authorization

`Workable.AspNetCore` does not create new authorization rules. It only supplies the identity and group information that Workable's existing authorization model consumes.

That means:

- system-level rules still come from `RequireAuthorization` and `ConfigureAuthorization`
- work-level rules still come from `WorkAuthorizationAttribute` or fluent authorization builders
- ASP.NET Core integration only controls how the current request is mapped into the actor, authenticated-caller, and group values used by those rules

See [Work Authorization](authorization.md) for the rule model itself.

## Relationship To The Built-In Adapters

`Workable.HttpApi`, `Workable.Mcp`, and `Workable.SignalR` all use this package's request-context creation and authenticated-principal mapping.

If you are building your own ASP.NET Core entry point, think of `Workable.AspNetCore` as the same plumbing those adapters already rely on.
