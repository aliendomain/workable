# ASP.NET Core Integration

`Workable.AspNetCore` is mostly a transitive package.

Most applications pick it up indirectly through `Workable.HttpApi`, `Workable.Mcp`, or `Workable.SignalR`. Reference it directly when you are building your own ASP.NET Core endpoints or your own transport and need to turn the current `HttpContext` into a Workable request context.

This package does not add Workable routes. It does not choose your authentication strategy. Its job is narrower: take the authenticated ASP.NET Core request you already have and translate it into Workable's actor, origin, authenticated-caller signal, and authorization-group model.

## When To Use It

Use `Workable.AspNetCore` directly when:

- your application has custom controllers or minimal APIs that queue work through `IHttpContextWorkCommandDispatcher`
- you are building your own ASP.NET Core transport instead of using Workable's built-in HTTP, MCP, or SignalR adapters
- you want Workable actor and authorization-group resolution to come from `HttpContext.User`

Do not add it just to host Workable. If you only use the built-in adapters, they already register it for you.

## What It Registers

Call `AddWorkableAspNetCoreAuthorization` to register the ASP.NET Core integration services:

```csharp
builder.Services.AddWorkableAspNetCoreAuthorization();
```

That registers:

- `IWorkActorFactory`
- `IWorkRequestContextFactory`
- `IHttpContextWorkCommandDispatcher`
- a default `IWorkAuthorizationGroupProvider`
- `IHttpContextAccessor` when one is not already registered

## Preferred HTTP Queueing Path

For custom ASP.NET Core endpoints that need to queue work, prefer `IHttpContextWorkCommandDispatcher`.

It wraps the common HTTP orchestration path:

- create a `WorkRequestContext` from the current `HttpContext`
- resolve the current actor, URL, and authenticated-caller signal
- dispatch the request through Workable using a standardized `WorkDispatchResult<T>`

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

Use `WorkDispatchCompletion.WaitForCompletion` when the caller needs the final output in the HTTP response instead of returning after acceptance.

## Request Context Creation

`IWorkRequestContextFactory` is still the lower-level entry point.

Use it when you need more than queueing, such as creating a session for direct query, worker action, catalog, or lifecycle access, or when you are building a custom transport that does not fit the dispatcher abstraction.

It builds a `WorkRequestContext` from the current `HttpContext`, the intended `WorkInvocationChannel`, and an optional short description of what the request is doing.

```csharp
app.MapPost("/welcome/{userId}", async (
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

The created context includes:

- a `WorkActor` derived from the current authenticated user
- a `WorkOrigin` that records the invocation channel and request URL
- `IsAuthenticated`, derived from the current ASP.NET Core principal
- any authorization group resolution performed by Workable later through the registered group provider

The `description` argument is optional. Supply it only when the endpoint has useful human-readable context worth preserving on the Workable origin.

That authenticated-caller signal is what lets work definitions use `AllowOperateToKnownAuthenticatedUsers()` without inventing a synthetic authorization group.

## Actor Resolution

`IWorkActorFactory` is responsible for turning the current user into a `WorkActor`.

By default:

- actor id comes from the first matching claim in `ActorIdClaimTypes`
- actor name comes from `HttpContext.User.Identity.Name`, then the configured `ActorNameClaimTypes`
- actor email comes from the configured `ActorEmailClaimTypes`
- anonymous users become `WorkActor.Unknown`

This is usually enough for custom endpoints that already have authenticated users and just need Workable to preserve that identity in queue origins, action history, and authorization evaluation. It also means a caller only qualifies for `AllowOperateToKnownAuthenticatedUsers()` when ASP.NET Core authentication succeeds and Workable can resolve a known actor.

## Authorization Group Resolution

The default `IWorkAuthorizationGroupProvider` reads group values from `HttpContext.User`.

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
});
```

`TransportAuthenticationScheme` matters when the Workable-facing surface should authenticate with a specific scheme even if the host application uses a different default for browser or cookie auth. When it is set, Workable explicitly authenticates that scheme and replaces `HttpContext.User` for the current request with the resulting principal.

This is especially useful when the host application's ambient or fallback scheme is not the scheme Workable should trust. A common example is an application that uses cookies or another default scheme for browser traffic, while Workable HTTP, MCP, or SignalR requests should authenticate against a bearer-token or transport-specific scheme.

In most hosts, `Configure<WorkableAspNetCoreAuthorizationOptions>(...)` is enough. Use `PostConfigure(...)` when you need the final override after another registration path has already configured the options and you want Workable's transport scheme to win.

That comes up most often in integration tests or platform-style hosts where Workable is already wired correctly by the target application and the test harness is layering one more authentication override on top. In that situation, registration order is effectively fixed, so `PostConfigure(...)` can be the only practical way to replace the final transport scheme.

## Custom Group Providers

If your application already has a richer authorization model, you can replace the default group provider:

```csharp
builder.Services.AddSingleton<IWorkAuthorizationGroupProvider, MyGroupProvider>();
builder.Services.AddWorkableAspNetCoreAuthorization();
```

Or register your provider after calling the Workable setup as the final override.

Use that path when Workable groups should come from something other than raw token claims, such as database lookups, tenant-aware role expansion, or application-specific permission projection.

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
