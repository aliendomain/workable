# Work Registration

## Intent

The host application owns Workable system configuration. Feature assemblies own the work they introduce.

Feature DLLs can define work once and let the host attach that work to the appropriate Workable system. The host can also register work directly when that is the simpler shape for the application.

Feature assemblies reference `Workable.Sdk`. Libraries that use a hosted work system without hosting one reference `Workable.Abstractions`. Host applications reference `Workable`.

## Host Configuration

Hosts create systems with `AddWorkableSystem`.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
});

services.AddWorkableSystem("email", builder =>
{
    builder.StartWithHost();
});
```

Each system receives an isolated catalog. Systems registered in the same host share the same application DI container.

## Feature Work

Feature assemblies register work with `AddWorkableWork`.

```csharp
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

Delegate registration and executor-type registration are both valid styles. Use the one that fits how you want to organize the feature. If you want the work to live in its own class or file, implement one of the executor interfaces and register that executor type. Executor classes can provide their registration metadata with `WorkMetadataAttribute`.

```csharp
[WorkMetadata("email.welcome.send", "Email:Lifecycle", "Sends a welcome email to a new user.")]
[WorkStart(WorkStartPolicy.StartAndReturnAfterAccepted)]
[WorkLogging(isEnabled: true)]
public sealed class SendWelcomeEmailExecutor
    : IWorkExecutor<SendWelcomeEmailArgs, SendWelcomeEmailResult>
{
    public Task<WorkExecutionResult<SendWelcomeEmailResult>> Execute(
        IWorkExecutionContext context,
        SendWelcomeEmailArgs input,
        CancellationToken cancellationToken)
        => Task.FromResult(
            WorkExecutionResult<SendWelcomeEmailResult>.Success(
                new SendWelcomeEmailResult(MessageId: Guid.NewGuid().ToString("N"))));
}

public sealed record SendWelcomeEmailArgs(string UserId);

public sealed record SendWelcomeEmailResult(string MessageId);

services.AddWorkableWork<SendWelcomeEmailExecutor>();
```

`WorkMetadataAttribute` is required when registering an executor without an explicit `WorkDefinition`. Its description is optional.

Typed executors can use:

- `IWorkExecutor<TInput>` when the work wants typed input and returns `WorkExecutionResult`.
- `IWorkExecutor<TInput, TOutput>` when the work wants typed input, typed output, and structured messages.
- `IWorkExecutor` when the work wants direct access to raw `WorkInput`.

Typed registration generates JSON schemas from `TInput` and `TOutput` when the work definition does not supply explicit schemas.
When host code queues work registered this way, `IWorkerHandle.WaitForCompletion<TOutput>()` deserializes the final output back to the same result type for the caller.

### Automatic Start

Use `WithAutomaticStart` when a work definition should be queued when the Workable system starts.

```csharp
services.AddWorkableWork<RefreshProductCatalogWork>(
    configure => configure.WithAutomaticStart());
```

Automatic start can queue multiple workers for the same definition.

```csharp
services.AddWorkableWork<RefreshProductCatalogWork>(
    configure => configure.WithAutomaticStart(instanceCount: 3));
```

Typed work can provide startup input with a factory. The factory runs when the system starts.

```csharp
services.AddWorkableWork<ProcessMailboxWork>(
    configure => configure.WithAutomaticStart(
        () => new ProcessMailboxInput("support")));
```

Automatically started work is accepted during system startup and then runs through the normal queue and dispatcher. It cannot use `WorkStartPolicy.StartAndReturnAfterCompleted`, because system startup does not wait for worker completion.

### Initialization

Use `WithInitialization` when work needs setup or validation before the executor runs.

```csharp
services.AddWorkableWork<SendWelcomeEmailExecutor>(
    configure => configure.WithInitialization<WelcomeEmailInitializer>());

public sealed class WelcomeEmailInitializer(UserTemplateCache cache) : IWorkInitializer<SendWelcomeEmailArgs>
{
    public async Task<WorkExecutionResult> Initialize(
        IWorkExecutionContext context,
        SendWelcomeEmailArgs input,
        CancellationToken cancellationToken = default)
    {
        await cache.Warm(input.UserId, cancellationToken);
        return WorkExecutionResult.Success();
    }
}
```

Initializers are resolved from an initialization scope. The executor is resolved afterward from a separate execution scope. Constructor dependencies are resolved from the host container, but scoped initializer dependencies are not shared with scoped executor dependencies.

Initialization timing can be controlled per initializer.

```csharp
services.AddWorkableWork<SendWelcomeEmailExecutor>(
    configure => configure
        .WithInitialization<EmailTemplateInitializer>(
            WorkInitializationTiming.OnceLazy,
            executionOrder: 10)
        .WithInitialization<RecipientValidationInitializer>(
            WorkInitializationTiming.OncePerWorker,
            executionOrder: 20));
```

`OncePerWorker` runs once for each worker before that worker executes. `OnceLazy` runs once per work definition the first time a worker needs it; competing workers wait behind a per-definition gate and later workers skip it after it succeeds. Typed initializers cannot use `OnceLazy` because they depend on worker input. If an initializer returns error messages, the worker fails and the executor is not invoked.

### Targeting Feature Work

Work registrations are unbound by default. That means the feature work is not pinned to one named Workable system at registration time. Instead, it is contributed to the host and can be included by any system that accepts work from feature assemblies.

In a host with one system, that usually means the work simply appears there. In a host with multiple systems, the same unbound feature work can appear in each system that includes contributed work.

Work registrations can target a named system.

```csharp
services.AddWorkableWork<SendDailyDigestExecutor>(
    systemName: "email");
```

Hosts can opt a system out of work from feature assemblies.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.IncludeContributedWork(false);
});
```

### Registering Directly In The Host

The host can also register work directly inside one system with `AddWorkableSystem(builder => builder.AddWork(...))`.

```csharp
services.AddWorkableSystem("email", builder =>
{
    builder.AddWork<SendDailyDigestExecutor>();
});
```

Work added through `IWorkSystemBuilder.AddWork(...)` is bound to that system by construction and does not need a `systemName`.

This shape means the host is choosing and registering that work directly. That can be a good fit when the host wants explicit control over which system owns the work. It is a different shape from feature-style `AddWorkableWork(...)` registration, where a feature package contributes work without hosting the system itself.

## Registering Workflows

Hosts can also register workflow definitions directly on `IWorkSystemBuilder`.

```csharp
services.AddWorkableSystem(builder =>
{
    var prepareDefinition = WorkDefinition.Create("orders.prepare");
    var emailDefinition = WorkDefinition.Create("orders.email");
    var invoiceDefinition = WorkDefinition.Create("orders.invoice");

    builder.AddWork(prepareDefinition, (_, _, _) =>
        Task.FromResult(WorkExecutionResult.Success()));
    builder.AddWork(emailDefinition, (_, _, _) =>
        Task.FromResult(WorkExecutionResult.Success()));
    builder.AddWork(invoiceDefinition, (_, _, _) =>
        Task.FromResult(WorkExecutionResult.Success()));

    builder.AddWorkflow(
        WorkflowDefinition.Create("orders.fulfillment", category: "Orders"),
        workflow => workflow
            .DispatchWork("prepare", prepareDefinition)
            .RunParallel("notify", parallel => parallel
                .DispatchWork("email", emailDefinition)
                .DispatchWork("invoice", invoiceDefinition))
            .Join("settle"));
});
```

Workflows are not executor classes. They are named orchestration definitions that dispatch existing work definitions through built-in step shapes.

Workflow authorization uses the same `IWorkAuthorizationBuilder` model as work registration. See [Workflows](workflows.md) for the current runtime scope, execution semantics, and durability behavior.

When several registrations share the same fluent work configuration or authorization, group them with `WithWorkDefaults(...)`.

```csharp
services.AddWorkableSystem("backstage", builder =>
{
    builder.RequireAuthorization();

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

The defaults run before each individual registration. If one work supplies its own `configure` or `authorize` callback, that callback runs after the group defaults and can override them.

Work-level operate grants can also add synchronous queue, worker-action, and reconfiguration requirements when a shared audience still needs per-request input checks.

The main reason to use this feature is to avoid cloning otherwise identical work definitions just to express narrower authorization slices. If `AdminSurveyWork` is one logical operation, it is usually better to keep one definition and discriminate by input such as `AreaKey` than to register separate definitions for every area-specific owner group.

```csharp
services.AddWorkableSystem("backstage", builder =>
{
    builder.RequireAuthorization();

    builder.AddWork<AdminSurveyWork>(
        authorize: auth => auth
            .AllowOperateToGroups("survey.admin")
            .AllowOperateToGroups(
                ["survey.north.owner"],
                operate => operate.WhenOperatingRequire<AdminSurveyArgs>(context =>
                    context.Input?.AreaKey == "north")));
});
```

In that example, the broad admin group keeps full access, while the narrower owner group can use the same work only for one area. These extra checks do not affect read visibility, but they can now target queueing, worker actions, worker reconfiguration, or definition reconfiguration depending on which requirement helper you choose.

If one definition needs separate audiences for queueing, worker actions, or reconfiguration, use the finer-grained helpers instead of cloning the definition:

```csharp
services.AddWorkableSystem("backstage", builder =>
{
    builder.RequireAuthorization();

    builder.AddWork<AdminSurveyWork>(
        authorize: auth => auth
            .AllowQueueToGroups("survey.queue")
            .AllowWorkerActionsToGroups("survey.ops")
            .AllowOperationsToGroups(
                ["survey.admin"],
                WorkOperationPermissions.Reconfigure));
});
```

In that shape, `AllowOperateToGroups(...)` remains the ergonomic full-access grant, while `AllowQueueToGroups(...)`, `AllowWorkerActionsToGroups(...)`, and `AllowOperationsToGroups(...)` let one definition express narrower operation rights without duplicating executor registrations.

For constrained grants:

- `WhenOperatingRequire(...)` applies to queueing, worker actions, and both reconfiguration surfaces
- `WhenQueueingRequire(...)` applies only to queueing
- `WhenWorkerActionsRequire(...)` applies only to worker actions
- `WhenReconfiguringRequire(...)` applies to worker and definition reconfiguration
- `WhenWorkerReconfiguringRequire(...)` applies only to worker reconfiguration
- `WhenDefinitionReconfiguringRequire(...)` applies only to definition reconfiguration

Typed worker-action and worker-reconfiguration requirements deserialize the worker's retained original input. Definition reconfiguration requirements inspect the reconfiguration change shape directly because definition reconfiguration does not carry work input.

## Work Definition Sources

Use a work definition source when a feature needs to create work definitions from configuration or runtime discovery before the Workable catalog is frozen.

```csharp
public sealed class MailboxWorkDefinitionSource(
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
                    description: $"Processes messages for {mailbox.Address}.",
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

services.AddWorkableWorkDefinitionSource<MailboxWorkDefinitionSource>();
```

Definition sources can also add service-backed executor work.

```csharp
public static class EmailWorkRegistration
{
    public static IServiceCollection AddEmailWork(this IServiceCollection services)
        => services
            .AddScoped<ProcessMailboxExecutor>()
            .AddWorkableWorkDefinitionSource<MailboxWorkDefinitionSource>();
}

public sealed class MailboxWorkDefinitionSource(
    IReadOnlyList<MailboxOptions> mailboxes) : IWorkDefinitionSource
{
    public Task DefineWork(
        IWorkDefinitionBuilder builder,
        CancellationToken cancellationToken = default)
    {
        foreach (var mailbox in mailboxes)
        {
            builder.AddWork<ProcessMailboxExecutor>(
                WorkDefinition.Create(
                    name: $"email.mailbox.process.{mailbox.Name}",
                    description: $"Processes messages for {mailbox.Address}.",
                    category: "Email:Mailboxes"));
        }

        return Task.CompletedTask;
    }
}

public sealed class ProcessMailboxExecutor(MailboxProcessor processor)
    : IWorkExecutor<ProcessMailboxInput>
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        ProcessMailboxInput input,
        CancellationToken cancellationToken)
    {
        await processor.Process(input, cancellationToken);
        return WorkExecutionResult.Success();
    }
}
```

Definition sources can use the same `WithWorkDefaults(...)` pattern when several generated definitions share the same fluent defaults.

```csharp
public sealed class MailboxWorkDefinitionSource(
    IReadOnlyList<MailboxOptions> mailboxes) : IWorkDefinitionSource
{
    public Task DefineWork(
        IWorkDefinitionBuilder builder,
        CancellationToken cancellationToken = default)
    {
        builder.WithWorkDefaults(
            register: work =>
            {
                foreach (var mailbox in mailboxes)
                {
                    work.AddWork<ProcessMailboxExecutor>(
                        WorkDefinition.Create(
                            name: $"email.mailbox.process.{mailbox.Name}",
                            description: $"Processes messages for {mailbox.Address}.",
                            category: "Email:Mailboxes"));
                }
            },
            configure: configure => configure.ConfigureLogging(level: LogLevel.Information),
            authorize: authorize => authorize.AllowOperateToGroups("mailbox.operators"));

        return Task.CompletedTask;
    }
}
```

Definition sources run when the system starts, before `IWorkCatalog.IsFrozen` becomes `true`. Definitions added by a source are normal catalog entries: they can be queued by name, queried, exposed through HTTP or MCP when allowed by invocation configuration, and configured like any other work definition.

Definition sources can target a named system.

```csharp
services.AddWorkableWorkDefinitionSource<MailboxWorkDefinitionSource>(
    systemName: "email");
```

A host can also attach a source directly while configuring a system.

```csharp
services.AddWorkableSystem("email", builder =>
{
    builder.AddWorkDefinitionSource<MailboxWorkDefinitionSource>();
});
```

When `systemName` is supplied, the source only contributes definitions to the matching named system. When the source is attached inside `AddWorkableSystem`, it contributes only to that system.

The source itself is resolved from a startup scope. Work definitions added by the source may use delegates or executor types.

Duplicate definition ids or duplicate definition names within one system catalog cause system start to fail.

Service-backed dynamic definitions require the executor type to be registered in the host container before the system starts. The executor is resolved from the worker execution scope when the worker runs.

This is different from `AddWorkableWork<TExecutor>()`. `AddWorkableWork<TExecutor>()` creates a concrete work definition immediately. For dynamic work, the source creates the definitions later, so the feature package usually registers the executor as a normal DI service and registers the source:

```csharp
services
    .AddScoped<ProcessMailboxExecutor>()
    .AddWorkableWorkDefinitionSource<MailboxWorkDefinitionSource>();
```

The application should normally consume that through a feature-owned extension method, such as `services.AddEmailWork()`, so callers do not need to know the executor lifetime or registration details.

Use delegate-backed dynamic definitions when the source should not register a dedicated executor service:

```csharp
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
```

## Startup Work Sources

Use a startup work source when a feature needs to queue work as part of system startup.

```csharp
public sealed class MailboxStartupWorkSource(
    IReadOnlyList<MailboxOptions> mailboxes) : IStartupWorkSource
{
    public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StartupWorkRequest> requests =
        [
            .. mailboxes.Select(mailbox =>
                StartupWorkRequest.ForName(
                    $"email.mailbox.process.{mailbox.Name}",
                    new ProcessMailboxInput(mailbox.Name)))
        ];

        return Task.FromResult(requests);
    }
}

services.AddWorkableStartupWorkSource<MailboxStartupWorkSource>();
```

Startup work sources run after definition sources have completed, the catalog is frozen, and automatic start work has been queued. A startup source returns queue requests by work name or definition id. Startup work cannot use `WorkStartPolicy.StartAndReturnAfterCompleted`; system start rejects that configuration because startup queueing cannot wait for worker completion. If any startup request cannot be queued, system start fails so the misconfiguration is visible.

Startup work sources can target a named system or be attached directly to a system.

```csharp
services.AddWorkableStartupWorkSource<MailboxStartupWorkSource>(
    systemName: "email");

services.AddWorkableSystem("email", builder =>
{
    builder.AddStartupWorkSource<MailboxStartupWorkSource>();
});
```

When `systemName` is supplied, the source only queues startup work for the matching named system. When the source is attached inside `AddWorkableSystem`, it runs only for that system.

The startup source itself is resolved from a startup scope.

## Registration Rules

- Host applications create systems with `AddWorkableSystem`.
- A host can register only one unnamed default system.
- Named Workable system names must be unique within the host.
- Feature assemblies add work with `AddWorkableWork`.
- Feature assemblies add generated definitions with `AddWorkableWorkDefinitionSource`.
- Feature assemblies add startup queue requests with `AddWorkableStartupWorkSource`.
- Non-host libraries can depend on `Workable.Abstractions` and accept `IWorkSystem` or `IWorkSystemRegistry` from the host.
- Feature assemblies define work independently of system configuration.
- Work definition sources run before the catalog is frozen.
- Startup work sources run after the catalog is frozen and after automatic start work has been queued.
- Work definition names must be unique within one system catalog, and work definition ids must also be unique within that catalog.
- Work definitions expose a name, category, and optional description for host browse/query screens.
- Attribute-only executor registration requires `WorkMetadataAttribute`.
- `WithAutomaticStart` queues work when the system starts.
- `WithInitialization` runs setup or validation before executor invocation.
- Unbound registrations are included in systems that accept work from feature assemblies.
- Named registrations are included only in the matching named system.
- A system can opt out of contributed feature work, contributed work definition sources, and contributed startup work sources with `IncludeContributedWork(false)`.
- Definition sources run once when a stopped system starts and are not rerun by a second `Start()` call while the system is already started.
- Startup work sources run each time a stopped system starts.
