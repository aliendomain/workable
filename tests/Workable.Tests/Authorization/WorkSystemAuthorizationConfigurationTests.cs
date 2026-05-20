using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkSystemAuthorizationConfigurationTests
{
    [Fact]
    public void WorkSystemRequiresAuthorizationByDefault()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => { })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.True(system.RequiresAuthorization);
    }

    [Fact]
    public void WorkSystemCanDisableAuthorization()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.RequireAuthorization(false))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.False(system.RequiresAuthorization);
    }

    [Fact]
    public void WorkSystemsConfigureAuthorizationIndependently()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests("open", builder => builder.RequireAuthorization(false))
            .AddDefaultWorkableSystemForAuthorizationTests("secure", builder => builder.RequireAuthorization())
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();

        Assert.True(registry.TryGet("open", out var open));
        Assert.True(registry.TryGet("secure", out var secure));
        Assert.False(open.RequiresAuthorization);
        Assert.True(secure.RequiresAuthorization);
    }

    [Fact]
    public void DirectInterfacesThrowWhenAuthorizationIsRequired()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(WorkDefinition.Create("secure"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Catalog);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Queue);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Workers);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Query);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Events);
    }

    [Fact]
    public void DirectInterfacesAreAvailableWhenAuthorizationIsDisabled()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(WorkDefinition.Create("open"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.NotNull(system.Catalog);
        Assert.NotNull(system.Queue);
        Assert.NotNull(system.Workers);
        Assert.NotNull(system.Query);
        Assert.NotNull(system.Events);
    }

    [Fact]
    public void CreateSessionProvidesInterfacesWhenAuthorizationIsRequired()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(WorkDefinition.Create("secure"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var session = system.CreateSession(new WorkActor(Id: "test-user"));

        Assert.NotNull(session.Catalog);
        Assert.NotNull(session.Queue);
        Assert.NotNull(session.Workers);
        Assert.NotNull(session.Query);
        Assert.NotNull(session.Events);
    }

    [Fact]
    public async Task AuthorizedSessionFiltersCatalogAndQueriesByReadScope()
    {
        var visible = PausedDefinition("visible.work");
        var hidden = PausedDefinition("hidden.work");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationScopeProvider>(new TestScopeProvider(new Dictionary<string, WorkAuthorizationScope>
            {
                ["operator"] = WorkAuthorizationScope.Create(
                    [visible.Id, hidden.Id],
                    [visible.Id, hidden.Id]),
                ["reader"] = WorkAuthorizationScope.Create([visible.Id], []),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(visible, SuccessfulWork)
                .AddWork(hidden, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var operatorSession = system.CreateSession(new WorkActor(Id: "operator"));
        await operatorSession.Queue.Enqueue(visible.Id);
        await operatorSession.Queue.Enqueue(hidden.Id);

        var readerSession = system.CreateSession(new WorkActor(Id: "reader"));

        Assert.Equal(visible.Id, Assert.Single(readerSession.Catalog.Definitions).Id);
        Assert.Equal(visible.Id, Assert.Single((await readerSession.Query.WorkDefinitions()).Definitions).Id);
        Assert.Equal(visible.Id, Assert.Single((await readerSession.Query.Workers(new WorkerCriteria(Take: 10))).Workers).DefinitionId);
        Assert.Null(await readerSession.Query.WorkInfo(hidden.Id));
    }

    [Fact]
    public async Task AuthorizedSessionRejectsQueueOutsideOperateScope()
    {
        var visible = PausedDefinition("visible.queue");
        var hidden = PausedDefinition("hidden.queue");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationScopeProvider>(new TestScopeProvider(new Dictionary<string, WorkAuthorizationScope>
            {
                ["operator"] = WorkAuthorizationScope.Create([visible.Id, hidden.Id], [visible.Id]),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(visible, SuccessfulWork)
                .AddWork(hidden, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var session = system.CreateSession(new WorkActor(Id: "operator"));

        var accepted = await session.Queue.Enqueue(visible.Id);
        var rejected = await session.Queue.Enqueue(hidden.Id);

        Assert.True(accepted.QueueOutcome.IsAccepted);
        Assert.Equal(WorkQueueStatus.NotFound, rejected.QueueOutcome.Status);
    }

    [Fact]
    public async Task AuthorizedSessionRejectsWorkerOperationsOutsideOperateScope()
    {
        var definition = PausedDefinition("hidden.operate");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationScopeProvider>(new TestScopeProvider(new Dictionary<string, WorkAuthorizationScope>
            {
                ["operator"] = WorkAuthorizationScope.Create([definition.Id], [definition.Id]),
                ["reader"] = WorkAuthorizationScope.Create([definition.Id], []),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var queued = await system.CreateSession(new WorkActor(Id: "operator")).Queue.Enqueue(definition.Id);
        var worker = await system.CreateSession(new WorkActor(Id: "operator")).Query.Worker(
            queued.WorkerId ?? throw new InvalidOperationException("Expected queued worker."));

        var outcome = await system.CreateSession(new WorkActor(Id: "reader")).Workers.Execute(
            worker?.Version ?? throw new InvalidOperationException("Expected worker."),
            WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void UnsecuredSessionDoesNotResolveAuthorizationScope()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationScopeProvider, ThrowingScopeProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(WorkDefinition.Create("open"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var session = system.CreateSession(new WorkActor(Id: "anyone"));

        Assert.NotNull(session.Catalog);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static WorkDefinition PausedDefinition(string name)
        => WorkDefinition.Create(
            name,
            configuration: WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart });

    private sealed class TestScopeProvider(IReadOnlyDictionary<string, WorkAuthorizationScope> scopes) : IWorkAuthorizationScopeProvider
    {
        public WorkAuthorizationScope GetScope(WorkActor actor, WorkSystemId systemId, string? systemName)
            => actor.Id is not null && scopes.TryGetValue(actor.Id, out var scope)
                ? scope
                : WorkAuthorizationScope.Empty;
    }

    private sealed class ThrowingScopeProvider : IWorkAuthorizationScopeProvider
    {
        public WorkAuthorizationScope GetScope(WorkActor actor, WorkSystemId systemId, string? systemName)
            => throw new InvalidOperationException("Authorization scope should not be resolved.");
    }
}

internal static class WorkSystemAuthorizationConfigurationTestExtensions
{
    public static IServiceCollection AddDefaultWorkableSystemForAuthorizationTests(
        this IServiceCollection services,
        Action<IWorkSystemBuilder> configure)
        => global::Workable.WorkableServiceCollectionExtensions.AddWorkableSystem(services, configure);

    public static IServiceCollection AddDefaultWorkableSystemForAuthorizationTests(
        this IServiceCollection services,
        string? name,
        Action<IWorkSystemBuilder> configure)
        => global::Workable.WorkableServiceCollectionExtensions.AddWorkableSystem(services, name, configure);
}
