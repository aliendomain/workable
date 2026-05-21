# Getting Started

The core Workable path uses three packages:

- `Workable.Sdk` for assemblies that define work.
- `Workable.Abstractions` for assemblies that use an already-hosted work system.
- `Workable` for applications that host and run work systems.

All three packages use the `Workable` namespace.

Optional adapter packages connect Workable to the edges of an application:

- Use `Workable.AspNetCore` when the host needs to create authenticated `WorkRequestContext` values from `HttpContext` for its own controllers, minimal APIs, or custom transports.
- `Workable.Views` provides shared component-view contracts and projections used by HTTP and SignalR adapters. Most applications receive it transitively through those adapters.
- Use `Workable.HttpApi` when the host wants Workable to provide standard HTTP routes for queueing work, querying workers, and sending worker actions such as pause, cancel, push, and purge.
- Use `Workable.Mcp` when the host wants authored work definitions, work-system query tools, and worker action tools to be available to an MCP client, such as an LLM tool host.
- Use `Workable.SignalR` when the host wants browser clients to receive realtime worker events and component-view updates.

The core host does not need these adapters unless it wants one of those integration points.

## Feature Assembly

A feature assembly defines work. It references `Workable.Sdk`.

```xml
<PackageReference Include="Workable.Sdk" Version="1.0.0" />
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

## Host Application

A host application creates Workable systems. It references `Workable`.

```xml
<PackageReference Include="Workable" Version="1.0.0" />
```

The host registers one or more systems:

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

Each system has its own catalog. Unbound work registrations are included in systems that accept work from feature assemblies. Work can also target a named system.

```csharp
services.AddWorkableWork<SendWelcomeEmailWork>(
    systemName: "email");
```

## Non-Host Library

A library can queue, query, or control work without hosting Workable. It references `Workable.Abstractions` and accepts `IWorkSystem` or `IWorkSystemRegistry` from the host application's DI container.

```xml
<PackageReference Include="Workable.Abstractions" Version="1.0.0" />
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
<PackageReference Include="Workable.AspNetCore" Version="1.0.0" />
```

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
    builder.RequireAuthorization();
});

services.AddWorkableAspNetCoreAuthorization();
```

Then create a session from the current request instead of queueing directly on `IWorkSystem`.

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

## Queue Work

Host code queues work through `IWorkSystem`. Injecting `IWorkSystem` gives the default system from `IWorkSystemRegistry`.

```csharp
var handle = await system.Queue.Enqueue(
    "email.welcome.send",
    new SendWelcomeEmailArgs("user-123"),
    cancellationToken: cancellationToken);

var completion = await handle.WaitForCompletion(cancellationToken);
```

The returned worker handle gives immediate queue outcome details and can be awaited for completion.

Use `IWorkSystemRegistry` instead when the host has multiple systems and the caller needs to choose by name or id.

The HTTP API adapter exposes the default system at the mapped prefix and named systems under `/systems/{systemName}`. The MCP adapter exposes one system per MCP endpoint; pass `systemName` to `MapWorkableMcp` when mapping an endpoint for a named system. The SignalR adapter exposes one realtime hub; pass `systemName` to hub methods when subscribing to a named system.

## Package Boundary

Feature assemblies reference only `Workable.Sdk` when they define work.

Non-host libraries reference `Workable.Abstractions` when they consume a work system supplied by the host.

Host applications reference `Workable` when they create systems, queue work, observe events, or control workers.

ASP.NET Core host applications can also reference `Workable.AspNetCore` when custom endpoints or transports should create authenticated `WorkRequestContext` values from `HttpContext`. Applications reference `Workable.HttpApi` only when they want Workable's built-in HTTP endpoints, `Workable.Mcp` only when they want an MCP server surface, and `Workable.SignalR` only when they want realtime client updates.

This keeps feature libraries independent of the host runtime while still allowing the host to compose all available work into the systems it owns.
