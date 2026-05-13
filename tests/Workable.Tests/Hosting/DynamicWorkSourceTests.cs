using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "DynamicWork")]
public sealed class DynamicWorkSourceTests
{
    [Fact]
    public async Task WorkDefinitionSourceAddsDefinitionsBeforeCatalogIsFrozen()
    {
        var services = new ServiceCollection()
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder.AddWorkDefinitionSource<RuntimeDefinitionSource>());
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.False(system.Catalog.TryGet("runtime.generated", out _));

        await system.Start();

        Assert.True(system.Catalog.IsFrozen);
        Assert.True(system.Catalog.TryGet("runtime.generated", out var definition));
        Assert.Equal("Dynamic:Definitions", definition.Category);
    }

    [Fact]
    public async Task WorkDefinitionSourceCanAddTypedDelegateWork()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWorkDefinitionSource<TypedDelegateDefinitionSource>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("runtime.typed.delegate", new EchoInput("hello"))).WaitForCompletion<EchoOutput>();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal("hello", completion.Output?.Message);
    }

    [Fact]
    public async Task WorkDefinitionSourceCanAddServiceBackedWorkWhenExecutorTypeIsRegistered()
    {
        var system = new ServiceCollection()
            .AddScoped<RuntimeEchoExecutor>()
            .AddWorkableSystem(builder => builder.AddWorkDefinitionSource<ServiceBackedDefinitionSource>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("runtime.service", new EchoInput("service"))).WaitForCompletion<EchoOutput>();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal("service", completion.Output?.Message);
    }

    [Fact]
    public async Task WorkDefinitionSourceUsesScopedServicesAndDisposesTheStartupScope()
    {
        var provider = new ServiceCollection()
            .AddSingleton<DynamicSourceTracker>()
            .AddScoped<ScopedDefinitionDependency>()
            .AddWorkableSystem(builder => builder.AddWorkDefinitionSource<ScopedDefinitionSource>())
            .BuildServiceProvider();
        var tracker = provider.GetRequiredService<DynamicSourceTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        Assert.True(tracker.ScopedDependencyWasResolved);
        await tracker.SourceScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SourceRegistrationUsesExistingContainerRegistration()
    {
        var tracker = new SourceInstanceTracker();
        var source = new ExistingRegisteredDefinitionSource(tracker);
        var system = new ServiceCollection()
            .AddSingleton(source)
            .AddWorkableWorkDefinitionSource<ExistingRegisteredDefinitionSource>()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        Assert.Same(source, tracker.Source);
    }

    [Fact]
    public async Task ContributedWorkDefinitionSourcesCanTargetNamedSystems()
    {
        var provider = new ServiceCollection()
            .AddWorkableWorkDefinitionSource<RuntimeDefinitionSource>(systemName: "remote")
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableSystem("remote", builder => builder.StartWithHost())
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var remote));

        await registry.Default.Start();
        await remote.Start();

        Assert.False(registry.Default.Catalog.TryGet("runtime.generated", out _));
        Assert.True(remote.Catalog.TryGet("runtime.generated", out _));
    }

    [Fact]
    public async Task SystemsCanOptOutOfContributedWorkDefinitionSources()
    {
        var system = new ServiceCollection()
            .AddWorkableWorkDefinitionSource<RuntimeDefinitionSource>()
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder.IncludeContributedWork(false))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        Assert.False(system.Catalog.TryGet("runtime.generated", out _));
    }

    [Fact]
    public async Task DuplicateDefinitionsFromSourcesAreRejectedWhenSystemStarts()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(WorkDefinition.Create("duplicate.runtime"), SuccessfulWork)
                .AddWorkDefinitionSource<DuplicateDefinitionSource>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Contains("Duplicate names", exception.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
    }

    [Fact]
    public async Task StartupWorkSourceQueuesWorkAfterRuntimeDefinitionsAreAvailable()
    {
        var provider = new ServiceCollection()
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder
                .AddWorkDefinitionSource<RuntimeDefinitionSource>()
                .AddStartupWorkSource<RuntimeStartupSource>())
            .BuildServiceProvider();
        var tracker = provider.GetRequiredService<DynamicSourceTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await using var completedSubscription = system.Events.Subscribe(new WorkEventFilter(EventType: "worker.completed"));
        await using var completedReader = completedSubscription.Read().GetAsyncEnumerator();

        await system.Start();
        await tracker.StartupWorkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ReadNext(completedReader);

        var workers = (await system.Query.QueryWorkers(new WorkerQuery())).Workers;
        var worker = Assert.Single(workers, worker => worker.DefinitionName == "runtime.generated");
        var snapshot = await system.Query.GetWorker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.Equal(WorkerState.Completed, worker.State);
        Assert.Equal(WorkInvocationChannel.DotNet, snapshot.Origin.Channel);
        Assert.Contains(nameof(RuntimeStartupSource), snapshot.Origin.Description);
    }

    [Fact]
    public async Task RestartRunsStartupWorkSourcesWithoutRedefiningRuntimeWork()
    {
        var provider = new ServiceCollection()
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder
                .AddWorkDefinitionSource<RuntimeDefinitionSource>()
                .AddStartupWorkSource<RuntimeStartupSource>())
            .BuildServiceProvider();
        var tracker = provider.GetRequiredService<DynamicSourceTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await system.Stop();
        await system.Start();

        var workers = await system.Query.QueryWorkers(new WorkerQuery(DefinitionName: "runtime.generated", Take: 10));

        Assert.Equal(1, tracker.DefinitionSourceRuns);
        Assert.Equal(2, tracker.StartupSourceRuns);
        Assert.Equal(1, workers.TotalCount);
    }

    [Fact]
    public async Task StartupWorkSourceCanQueueByDefinitionId()
    {
        var tracker = new DynamicSourceTracker();
        var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder
                .AddWork(
                    WorkDefinition.Create("startup.by-id", id: StartupByIdSource.DefinitionId),
                    (context, input, cancellationToken) =>
                    {
                        tracker.StartupWorkCompleted.TrySetResult();
                        return Task.FromResult(WorkExecutionResult.Success());
                    })
                .AddStartupWorkSource<StartupByIdSource>())
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await tracker.StartupWorkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var workers = (await system.Query.QueryWorkers(new WorkerQuery())).Workers;
        Assert.Contains(workers, worker => worker.DefinitionId == StartupByIdSource.DefinitionId);
    }

    [Fact]
    public async Task StartupWorkSourceRejectedQueueRequestFailsSystemStart()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddStartupWorkSource<MissingStartupWorkSource>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Contains("was not found", exception.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
    }

    [Fact]
    public async Task StartupWorkSourceRejectsDefinitionConfiguredToWaitForCompletion()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(
                    WorkDefinition.Create(
                        "startup.wait.definition",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = new WorkStartConfiguration
                            {
                                Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
                            },
                        }),
                    SuccessfulWork)
                .AddStartupWorkSource<WaitForCompletionStartupWorkSource>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Contains(nameof(WorkStartPolicy.StartAndReturnAfterCompleted), exception.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
    }

    [Fact]
    public async Task StartupWorkSourceRejectsRequestOptionsConfiguredToWaitForCompletion()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(WorkDefinition.Create("startup.wait.options"), SuccessfulWork)
                .AddStartupWorkSource<WaitForCompletionOptionsStartupWorkSource>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Contains(nameof(WorkStartPolicy.StartAndReturnAfterCompleted), exception.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
    }

    [Fact]
    public async Task StartDoesNotRunSourcesAgainWhenSystemIsAlreadyStarted()
    {
        var provider = new ServiceCollection()
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder
                .AddWorkDefinitionSource<RuntimeDefinitionSource>()
                .AddStartupWorkSource<RuntimeStartupSource>())
            .BuildServiceProvider();
        var tracker = provider.GetRequiredService<DynamicSourceTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await system.Start();

        Assert.Equal(1, tracker.DefinitionSourceRuns);
        Assert.Equal(1, tracker.StartupSourceRuns);
    }

    [Fact]
    public async Task StartupWorkSourcesRunAgainWhenStoppedSystemStartsAgain()
    {
        var provider = new ServiceCollection()
            .AddSingleton<DynamicSourceTracker>()
            .AddWorkableSystem(builder => builder
                .AddWorkDefinitionSource<RuntimeDefinitionSource>()
                .AddStartupWorkSource<RuntimeStartupSource>())
            .BuildServiceProvider();
        var tracker = provider.GetRequiredService<DynamicSourceTracker>();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await system.Stop();
        await system.Start();

        Assert.Equal(1, tracker.DefinitionSourceRuns);
        Assert.Equal(2, tracker.StartupSourceRuns);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private sealed record EchoInput(string Message);

    private sealed record EchoOutput(string Message);

    private sealed class DynamicSourceTracker
    {
        public int DefinitionSourceRuns { get; private set; }

        public int StartupSourceRuns { get; private set; }

        public bool ScopedDependencyWasResolved { get; private set; }

        public TaskCompletionSource StartupWorkCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SourceScopeDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordDefinitionSourceRun()
            => this.DefinitionSourceRuns++;

        public void RecordStartupSourceRun()
            => this.StartupSourceRuns++;

        public void RecordScopedDependencyResolved()
            => this.ScopedDependencyWasResolved = true;
    }

    private sealed class SourceInstanceTracker
    {
        public object? Source { get; private set; }

        public void Record(object source)
            => this.Source = source;
    }

    private sealed class RuntimeDefinitionSource(DynamicSourceTracker tracker) : IWorkDefinitionSource
    {
        public Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default)
        {
            tracker.RecordDefinitionSourceRun();
            builder.AddWork(
                WorkDefinition.Create("runtime.generated", category: "Dynamic:Definitions"),
                (context, input, cancellationToken) =>
                {
                    tracker.StartupWorkCompleted.TrySetResult();
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            return Task.CompletedTask;
        }
    }

    private sealed class TypedDelegateDefinitionSource : IWorkDefinitionSource
    {
        public Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default)
        {
            builder.AddWork<EchoInput, EchoOutput>(
                WorkDefinition.Create("runtime.typed.delegate"),
                (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult<EchoOutput>.Success(new EchoOutput(input.Message))));
            return Task.CompletedTask;
        }
    }

    private sealed class ServiceBackedDefinitionSource : IWorkDefinitionSource
    {
        public Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default)
        {
            builder.AddWork<RuntimeEchoExecutor>(WorkDefinition.Create("runtime.service"));
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeEchoExecutor : IWorkExecutor<EchoInput, EchoOutput>
    {
        public Task<WorkExecutionResult<EchoOutput>> Execute(IWorkExecutionContext context, EchoInput input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult<EchoOutput>.Success(new EchoOutput(input.Message)));
    }

    private sealed class ScopedDefinitionSource(
        ScopedDefinitionDependency dependency,
        DynamicSourceTracker tracker) : IWorkDefinitionSource
    {
        public Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default)
        {
            dependency.Use();
            tracker.RecordScopedDependencyResolved();
            return Task.CompletedTask;
        }
    }

    private sealed class ScopedDefinitionDependency(DynamicSourceTracker tracker) : IAsyncDisposable
    {
        public void Use()
        {
        }

        public ValueTask DisposeAsync()
        {
            tracker.SourceScopeDisposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExistingRegisteredDefinitionSource(SourceInstanceTracker tracker) : IWorkDefinitionSource
    {
        public Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default)
        {
            tracker.Record(this);
            return Task.CompletedTask;
        }
    }

    private sealed class DuplicateDefinitionSource : IWorkDefinitionSource
    {
        public Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default)
        {
            builder.AddWork(WorkDefinition.Create("duplicate.runtime"), SuccessfulWork);
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeStartupSource(DynamicSourceTracker tracker) : IStartupWorkSource
    {
        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default)
        {
            tracker.RecordStartupSourceRun();
            return Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
                [StartupWorkRequest.ForName("runtime.generated")]);
        }
    }

    private sealed class StartupByIdSource(DynamicSourceTracker tracker) : IStartupWorkSource
    {
        public static WorkDefinitionId DefinitionId { get; } = WorkDefinitionId.New();

        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default)
        {
            tracker.RecordStartupSourceRun();
            return Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
                [StartupWorkRequest.ForDefinition(DefinitionId)]);
        }
    }

    private sealed class MissingStartupWorkSource : IStartupWorkSource
    {
        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
                [StartupWorkRequest.ForName("missing.startup")]);
    }

    private sealed class WaitForCompletionStartupWorkSource : IStartupWorkSource
    {
        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
                [StartupWorkRequest.ForName("startup.wait.definition")]);
    }

    private sealed class WaitForCompletionOptionsStartupWorkSource : IStartupWorkSource
    {
        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartupWorkRequest>>(
                [
                    StartupWorkRequest.ForName(
                        "startup.wait.options",
                        options: new WorkerOptions(
                            Configuration: WorkConfiguration.Default with
                            {
                                Start = new WorkStartConfiguration
                                {
                                    Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
                                },
                            })),
                ]);
    }
}
