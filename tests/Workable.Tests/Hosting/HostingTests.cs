using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class HostingTests
{
    [Fact]
    public void DefaultAndNamedSystemsCanBeRegisteredSideBySide()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(WorkDefinition.Create("default", "Default work."), SuccessfulWork))
            .AddWorkableSystem("background", builder => builder.AddWork(WorkDefinition.Create("background", "Background work."), SuccessfulWork));

        var registry = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>();

        Assert.Null(registry.Default.Name);
        Assert.True(registry.TryGet("background", out var background));
        Assert.Equal("background", background.Name);
        Assert.Equal(2, registry.Systems.Count);
    }

    [Fact]
    public void DefaultWorkSystemCanBeInjectedDirectly()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(WorkDefinition.Create("default", "Default work."), SuccessfulWork))
            .AddWorkableSystem("background", builder => builder.AddWork(WorkDefinition.Create("background", "Background work."), SuccessfulWork));

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.Same(registry.Default, system);
        Assert.Null(system.Name);
    }

    [Fact]
    public async Task WorkExecutionReceivesScopedServicesFromHostContainer()
    {
        var definition = WorkDefinition.Create("scoped", "Uses a scoped service.");
        var services = new ServiceCollection();
        services.AddScoped<ScopedMarker>();
        services.AddWorkableSystem(builder => builder.AddWork<ScopedExecutor>(definition));

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        var handle = await system.Queue.Enqueue("scoped");
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.True(completion.Output?.ToValue<ScopedResult>()?.Resolved);
    }

    [Fact]
    public async Task WorkExecutionDisposesAsyncScopedServicesAfterExecution()
    {
        var definition = WorkDefinition.Create("async-scoped", "Disposes async scoped services.");
        var services = new ServiceCollection();
        services.AddSingleton<AsyncDisposeTracker>();
        services.AddScoped<AsyncScopedMarker>();
        services.AddWorkableSystem(builder => builder.AddWork<AsyncScopedExecutor>(definition));

        var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<AsyncDisposeTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        var handle = await system.Queue.Enqueue("async-scoped");
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        await tracker.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WorkCanBeContributedWithoutConfiguringASystem()
    {
        var contributed = WorkDefinition.Create("feature.work", "Registered by a feature assembly.");

        var services = new ServiceCollection()
            .AddWorkableWork(contributed, SuccessfulWork)
            .AddWorkableSystem(builder => builder.StartWithHost());

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(system.Catalog.TryGet("feature.work", out var definition));
        Assert.Equal(contributed.Id, definition.Id);
    }

    [Fact]
    public void AttributeOnlyContributedWorkUsesMetadataForDefinition()
    {
        var services = new ServiceCollection()
            .AddWorkableWork<AttributedContributionWork>()
            .AddWorkableSystem(builder => builder.StartWithHost());

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(system.Catalog.TryGet("feature.attributed", out var definition));
        Assert.Equal("Features:Attributed", definition.Category);
        Assert.Equal("Registered entirely through attributes.", definition.Description);
        Assert.Equal(WorkStartPolicy.DoNotStart, definition.Configuration.Start.Policy);
    }

    [Fact]
    public void AttributeOnlySystemWorkUsesMetadataForDefinition()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedSystemWork>());

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.True(system.Catalog.TryGet("system.attributed", out var definition));
        Assert.Equal("Systems:Attributed", definition.Category);
        Assert.Null(definition.Description);
    }

    [Fact]
    public void AttributeOnlyRegistrationRequiresWorkMetadata()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddWorkableWork<MissingMetadataWork>());

        Assert.Contains(nameof(WorkMetadataAttribute), exception.Message);
    }

    [Fact]
    public void ContributionsCanTargetNamedSystems()
    {
        var shared = WorkDefinition.Create("shared.work", "Available everywhere.");
        var targeted = WorkDefinition.Create("targeted.work", "Available to one system.");

        var services = new ServiceCollection()
            .AddWorkableWork(shared, SuccessfulWork)
            .AddWorkableWork(targeted, SuccessfulWork, systemName: "remote")
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem("remote", builder => builder.StartWithHost());

        var registry = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var remote));

        Assert.True(registry.Default.Catalog.TryGet("shared.work", out _));
        Assert.False(registry.Default.Catalog.TryGet("targeted.work", out _));
        Assert.True(remote.Catalog.TryGet("shared.work", out _));
        Assert.True(remote.Catalog.TryGet("targeted.work", out _));
    }

    [Fact]
    public void SystemsCanOptOutOfContributedWork()
    {
        var contributed = WorkDefinition.Create("feature.work", "Registered by a feature assembly.");

        var services = new ServiceCollection()
            .AddWorkableWork(contributed, SuccessfulWork)
            .AddWorkableSystem(builder => builder.IncludeContributedWork(false));

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.False(system.Catalog.TryGet("feature.work", out _));
    }

    [Fact]
    public async Task DirectDotNetQueueUsesConfiguredOriginProvider()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .UseDotNetOriginProvider(_ => new StaticDotNetOriginProvider())
                .AddWork(WorkDefinition.Create("origin.work"), SuccessfulWork));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = await system.Queue.Enqueue("origin.work");
        var worker = await system.Query.Worker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker."));

        Assert.NotNull(worker);
        Assert.Equal(WorkInvocationChannel.DotNet, worker.Origin.Channel);
        Assert.Equal("configured-user", worker.Origin.Actor.Id);
        Assert.Equal("Queue work 'origin.work' through .NET.", worker.Origin.Description);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed class ScopedMarker;

    private sealed record ScopedResult(bool Resolved);

    private sealed class ScopedExecutor(ScopedMarker marker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            var resolvedFromContext = context.Services.GetRequiredService<ScopedMarker>();
            return Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new ScopedResult(ReferenceEquals(marker, resolvedFromContext)))));
        }
    }

    [WorkMetadata("feature.attributed", "Features:Attributed", "Registered entirely through attributes.")]
    [WorkStart(WorkStartPolicy.DoNotStart)]
    private sealed class AttributedContributionWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    [WorkMetadata("system.attributed", "Systems:Attributed")]
    private sealed class AttributedSystemWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class MissingMetadataWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class AsyncDisposeTracker
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AsyncScopedMarker(AsyncDisposeTracker tracker) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            tracker.Disposed.SetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncScopedExecutor(AsyncScopedMarker marker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            _ = marker;
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class StaticDotNetOriginProvider : IDotNetWorkOriginProvider
    {
        public WorkOrigin CreateOrigin(string description)
            => WorkOrigin.Create(
                WorkInvocationChannel.DotNet,
                new WorkActor(Id: "configured-user", Name: "Configured User"),
                description);
    }
}
