# Getting Started

Workable fits applications where work needs identity, state, visibility, and runtime control instead of being treated like an anonymous background task. It is especially useful when feature code should author work locally, while the host application decides how that work is started, retried, observed, authorized, and exposed through transport adapters.

If you are evaluating whether it is the right abstraction, think in terms of durable operational work: jobs you may need to query later, cancel, retry, inspect in an admin surface, or invoke through more than one channel.

The core Workable path uses three packages:

- `Workable.Sdk` for assemblies that define work.
- `Workable.Abstractions` for assemblies that use an already-hosted work system.
- `Workable` for applications that host and run work systems.

All three packages use the `Workable` namespace.

Optional packages add persistence, security, transport, and realtime integrations:

- Use [`Workable.SqlServer`](../../packages/extensions/sqlserver/README.md) when the host wants SQL Server persistence for durable queueing and completion, durable workflows, persistence-backed idempotency, and persistence-backed concurrency.
- Use `Workable.AspNetCore` when the host needs its own controllers, minimal APIs, or custom transports to dispatch Workable work from the current `HttpContext`.
- Use `Workable.Entra` when an ASP.NET Core target app should validate Microsoft Entra ID bearer tokens for Workable HTTP, MCP, and SignalR adapter calls.
- `Workable.Views` provides shared component-view contracts and projections used by HTTP and SignalR adapters. Most applications receive it transitively through `Workable.HttpApi` or `Workable.SignalR` instead of referencing it directly.
- Use `Workable.HttpApi` when the host wants Workable to provide standard HTTP routes for queueing work, starting and inspecting workflow runs, querying workers, and sending worker actions such as pause, cancel, push, and purge.
- Use `Workable.Mcp` when the host wants authored work definitions, work-system and workflow query tools, and worker or workflow action tools to be available to an MCP client, such as an LLM tool host.
- Use `Workable.SignalR` when the host wants browser clients to receive realtime worker and workflow events plus component-view updates.

The core host does not need these packages unless it wants one of those integration points.

## Hosting Workable Systems in your Application

You can host one Workable system or several. An unnamed registration is the explicit default system. If every system is named, the first registered system becomes the default. Injecting `IWorkSystem` gives that default system directly, while `IWorkSystemRegistry` lets host code choose a specific named system by name.

Multiple systems are useful when different areas of the application need isolation. For example, you might separate customer-facing work from internal operations work, give each system different authorization or retention settings, or expose only one system to a particular client surface. If you only need one catalog and one set of runtime policies, the default system is usually enough.

```xml
<PackageReference Include="Workable" Version="<current-version>" />
```

Replace `<current-version>` with the Workable package version you want to install.

Example registration:

```csharp
using Workable;

services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
});

services.AddWorkableSystem("email", builder =>
{
    builder.StartWithHost();
});
```

When a subset of registrations in one system share the same work-level configuration or authorization, use `WithWorkDefaults(...)` to keep the registration block compact.

```csharp
services.AddWorkableSystem("backstage", builder =>
{
    builder.StartWithHost();
    builder.RequireAuthorization();

    builder.AddWork<SubmitSurveyWork>(
        authorize: auth => auth.AllowOperateToKnownAuthenticatedUsers());

    builder.WithWorkDefaults(
        register: work => work
            .AddWork<CreateSurveyAreaWork>()
            .AddWork<CreateSurveyTemplateWork>()
            .AddWork<DeleteSurveyAreaWork>(),
        authorize: auth => auth.AllowOperateToGroups("survey.admin"));
});
```

### Lifecycle Options

The system builder has three lifecycle controls:

- `StartWithHost(bool enabled = true)`: starts the system automatically with the host instead of requiring a manual start.
- `UseShutdownGracePeriod(TimeSpan gracePeriod)`: sets an explicit shutdown grace period for that system.
- `UseShutdownGracePeriodRatio(double hostShutdownTimeoutRatio)`: keeps the system shutdown grace period relative to the host shutdown timeout.

Hosts can change the shutdown grace period. By default, Workable uses 80% of the
.NET generic host shutdown timeout when host options are available. If Workable
is used outside a generic host, the fallback default is 15 seconds.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
    builder.UseShutdownGracePeriod(TimeSpan.FromSeconds(30));
});
```

You can also keep the grace period relative to the host timeout. Ratios must be
greater than zero and cannot exceed 90%.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
    builder.UseShutdownGracePeriodRatio(0.75);
});
```

## Feature Assembly

A feature assembly is an assembly that authors and registers work definitions. It references `Workable.Sdk`.

It is not the assembly that hosts the Workable runtime. The host application references `Workable`, builds one or more systems, and decides how that authored work is started, configured, exposed, and observed.

```xml
<PackageReference Include="Workable.Sdk" Version="<current-version>" />
```

Feature assemblies can register work with a delegate:

```csharp
using Workable;

services.AddWorkableWork(
    WorkDefinition.Create(
        name: "email.welcome.send",
        description: "Sends a welcome email to a new user.",
        category: "Email:Lifecycle"),
    execute: async (context, input, cancellation) =>
    {
        await Task.CompletedTask;
        return WorkExecutionResult.Success();
    });
```

Feature assemblies can also implement typed executor interfaces. Workable generates input and output schemas from the typed records and adapts the executor to the core runtime contract.

```csharp
using Workable;

[WorkMetadata("email.welcome.send", "Email:Lifecycle", "Sends a welcome email to a new user.")]
public sealed class SendWelcomeEmailWork
    : IWorkExecutor<SendWelcomeEmailArgs, SendWelcomeEmailResult>
{
    public Task<WorkExecutionResult<SendWelcomeEmailResult>> Execute(
        IWorkExecutionContext context,
        SendWelcomeEmailArgs input,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(
            WorkExecutionResult<SendWelcomeEmailResult>.Success(
                new SendWelcomeEmailResult(MessageId: Guid.NewGuid().ToString("N"))));
    }
}

public sealed record SendWelcomeEmailArgs(string UserId);

public sealed record SendWelcomeEmailResult(string MessageId);

services.AddWorkableWork<SendWelcomeEmailWork>();
```

When host code queues typed work, it can await a typed completion and read the deserialized result back from the worker handle:

```csharp
var handle = await system.Queue.Enqueue(
    "email.welcome.send",
    new SendWelcomeEmailArgs("user-123"),
    cancellationToken: cancellationToken);

WorkCompletion<SendWelcomeEmailResult> completion =
    await handle.WaitForCompletion<SendWelcomeEmailResult>(cancellationToken);

if (completion.IsCompletedSuccessfully)
{
    string messageId = completion.Output!.MessageId;
}
```

The queueing guide covers immediate queue outcomes, raw `WorkOutput`, and typed completions in more detail.

Registration-time fluent options can attach behavior such as automatic startup or initialization.

```csharp
services.AddWorkableWork<SendWelcomeEmailWork>(
    configure => configure.WithInitialization<WelcomeEmailInitializer>());

services.AddWorkableWork<RefreshProductCatalogWork>(
    configure => configure.WithAutomaticStart());
```

Feature assemblies can also provide work definition sources when the set of definitions comes from feature configuration.

```csharp
public sealed class EmailWorkDefinitionSource(
    IReadOnlyList<MailboxOptions> mailboxes) : IWorkDefinitionSource
{
    public Task DefineWork(
        IWorkDefinitionBuilder builder,
        CancellationToken cancellationToken = default)
    {
        foreach (var mailbox in mailboxes)
        {
            builder.AddWork<ProcessMailboxInput>(
                WorkDefinition.Create(
                    name: $"email.mailbox.process.{mailbox.Name}",
                    category: "Email:Mailboxes"),
                async (context, input, cancellationToken) =>
                {
                    var processor = context.Services.GetRequiredService<MailboxProcessor>();
                    await processor.Process(input, cancellationToken);
                    return WorkExecutionResult.Success();
                });
        }

        return Task.CompletedTask;
    }
}

services.AddWorkableWorkDefinitionSource<EmailWorkDefinitionSource>();
```

Feature assemblies can provide startup work sources when work should be queued as the system starts.

```csharp
public sealed class EmailStartupWorkSource(
    IReadOnlyList<MailboxOptions> mailboxes) : IStartupWorkSource
{
    public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
            [.. mailboxes.Select(mailbox =>
                StartupWorkRequest.ForName(
                    $"email.mailbox.process.{mailbox.Name}",
                    new ProcessMailboxInput(mailbox.Name)))]);
}

services.AddWorkableStartupWorkSource<EmailStartupWorkSource>();
```

## Non-Host Library

A library can queue, query, or control work without hosting Workable. It references `Workable.Abstractions` and accepts `IWorkSystem` or `IWorkSystemRegistry` from the host application's DI container.

```xml
<PackageReference Include="Workable.Abstractions" Version="<current-version>" />
```

```csharp
public sealed class WelcomeEmailService(IWorkSystem workSystem)
{
    public Task<IWorkerHandle> SendWelcomeEmail(
        string userId,
        CancellationToken cancellationToken)
        => workSystem.Queue.Enqueue(
            "email.welcome.send",
            new SendWelcomeEmailArgs(userId),
            cancellationToken: cancellationToken);
}
```

ASP.NET Core hosts that queue work from their own controllers or minimal API routes can reference `Workable.AspNetCore` and register HTTP-context request-context services. Use this when the work should record who requested it without exposing Workable's standard HTTP API endpoints.

```xml
<PackageReference Include="Workable.AspNetCore" Version="<current-version>" />
```

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
    builder.RequireAuthorization();
});

services.AddWorkableAspNetCoreAuthorization();
```

For custom HTTP endpoints that only need to queue work, prefer `IHttpContextWorkCommandDispatcher`.

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

The `description` argument is optional. Keep it when the endpoint should preserve user-facing request context on the queued worker origin, or omit it when the route shape and actor identity already tell the whole story.

Drop down to `IWorkRequestContextFactory` and `IWorkSystem.CreateSession(...)` when the endpoint needs broader session work such as direct query, worker action, catalog, or lifecycle access instead of just dispatching queued work.

If the ASP.NET Core host should validate Microsoft Entra ID bearer tokens for Workable adapters, reference `Workable.Entra` and configure the target app tenant/audience:

```xml
<PackageReference Include="Workable.Entra" Version="<current-version>" />
```

```csharp
builder.Services.AddWorkableEntraAuthorization(
    builder.Configuration.GetSection(WorkableEntraAuthorizationDefaults.ConfigurationSectionName));

services.AddWorkableSystem(builder =>
{
    builder.ConfigureAuthorization(auth => auth
        .AllowReadAllWorkToGroups("11111111-2222-3333-4444-555555555555")
        .AllowOperateAllWorkToGroups("11111111-2222-3333-4444-555555555555"));
});
```

## Queue Work

Host code queues work through `IWorkSystem`. Injecting `IWorkSystem` gives the default system from `IWorkSystemRegistry`.

```csharp
var handle = await system.Queue.Enqueue(
    "email.welcome.send",
    new SendWelcomeEmailArgs("user-123"),
    cancellationToken: cancellationToken);

WorkCompletion<SendWelcomeEmailResult> completion =
    await handle.WaitForCompletion<SendWelcomeEmailResult>(cancellationToken);
```

The returned worker handle gives immediate queue outcome details and can be awaited for completion, including typed output when the work returns a typed result.

Use `IWorkSystemRegistry` instead when the host has multiple systems and the caller needs to choose by name.

The HTTP API adapter exposes the default system at the mapped prefix and named systems under `/systems/{systemName}`. The MCP adapter exposes one system per MCP endpoint; pass `systemName` to `MapWorkableMcp` when mapping an endpoint for a named system. The SignalR adapter exposes one realtime hub; pass `systemName` to hub methods when subscribing to a named system.

## Package Boundary

Feature assemblies reference only `Workable.Sdk` when they define work.

Non-host libraries reference `Workable.Abstractions` when they consume a work system supplied by the host.

Host applications reference `Workable` when they create systems, queue work, observe events, or control workers.

ASP.NET Core host applications can also reference `Workable.AspNetCore` when custom endpoints or transports should dispatch work from the current `HttpContext`, or `Workable.Entra` when Workable adapter requests should validate Microsoft Entra target-audience bearer tokens. Applications reference `Workable.HttpApi` only when they want Workable's built-in HTTP endpoints, `Workable.Mcp` only when they want an MCP server surface, and `Workable.SignalR` only when they want realtime client updates.

This keeps feature libraries independent of the host runtime while still allowing the host to compose all available work into the systems it owns.

## Good Next Reads

- Read [Implementing Work](implementing-work.md) to understand what executor code can do through `IWorkExecutionContext`, and how pause, cancel, interruption, failure, and exceptions behave at runtime.
- Read [Registration](registration.md) to go deeper on authored work, definition sources, startup work, and named systems.
- Read [Queueing](queueing.md) to go deeper on inputs, queue options, request context, and waiting for completion.
