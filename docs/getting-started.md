# Getting Started

The core Workable path uses three packages:

- `Workable.Sdk` for assemblies that define work.
- `Workable.Abstractions` for assemblies that use an already-hosted work system.
- `Workable` for applications that host and run work systems.

All three packages use the `Workable` namespace.

Optional adapter packages connect Workable to the edges of an application:

- Use `Workable.AspNetCore` when the host queues work from its own ASP.NET Core controllers or minimal API routes and wants worker origins to include the authenticated HTTP user and request path.
- Use `Workable.HttpApi` when the host wants Workable to provide standard HTTP routes for queueing work, querying workers, and sending worker actions such as pause, cancel, push, and purge.
- Use `Workable.Mcp` when the host wants authored work definitions, work-system query tools, and worker action tools to be available to an MCP client, such as an LLM tool host.

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

Hosts can change the shutdown grace period. The default is 15 seconds.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
    builder.UseShutdownGracePeriod(TimeSpan.FromSeconds(30));
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

ASP.NET Core hosts that queue work from their own controllers or minimal API routes can reference `Workable.AspNetCore` and register HTTP-context origins. Use this when the work should record who requested it without exposing Workable's standard HTTP API endpoints.

```xml
<PackageReference Include="Workable.AspNetCore" Version="1.0.0" />
```

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
});

services.AddWorkableAspNetCoreOrigins();
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

The HTTP API adapter exposes the default system at the mapped prefix and named systems under `/systems/{systemName}`. The MCP adapter exposes one system per MCP endpoint; pass `systemName` to `MapWorkableMcp` when mapping an endpoint for a named system.

## Package Boundary

Feature assemblies reference only `Workable.Sdk` when they define work.

Non-host libraries reference `Workable.Abstractions` when they consume a work system supplied by the host.

Host applications reference `Workable` when they create systems, queue work, observe events, or control workers.

ASP.NET Core host applications can also reference `Workable.AspNetCore` when direct .NET queue calls should record actor information from `HttpContext.User`. Applications reference `Workable.HttpApi` only when they want Workable's built-in HTTP endpoints, and `Workable.Mcp` only when they want an MCP server surface.

This keeps feature libraries independent of the host runtime while still allowing the host to compose all available work into the systems it owns.
