using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkInitialization")]
public sealed class WorkInitializationTests
{
    [Fact]
    public async Task InitializationRunsBeforeExecutor()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<InitializedExecutor>(
                WorkDefinition.Create("initialized.work"),
                configure => configure.WithInitialization<RecordingInitializer>()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("initialized.work")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(["initializer", "executor"], tracker.Events);
    }

    [Fact]
    public async Task TypedInitializationReceivesWorkInput()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<TypedInitializedExecutor>(
                WorkDefinition.Create("typed.initialized"),
                configure => configure.WithInitialization<TypedRecordingInitializer>()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("typed.initialized", new InitializationInput("alpha"))).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(["initializer:alpha", "executor:alpha"], tracker.Events);
    }

    [Fact]
    public async Task TypedInitializationSelectsMatchingInputType()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<InitializedExecutor>(
                WorkDefinition.Create("multi-typed.initialized"),
                configure => configure.WithInitialization<MultiTypedRecordingInitializer>()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("multi-typed.initialized", new OtherInitializationInput("bravo"))).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(["initializer:other:bravo", "executor"], tracker.Events);
    }

    [Fact]
    public async Task InitializationFailureFailsWorkerWithoutExecutingWork()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<InitializedExecutor>(
                WorkDefinition.Create("initialization.fails"),
                configure => configure.WithInitialization<FailingInitializer>()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("initialization.fails")).WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(["initializer"], tracker.Events);
        Assert.Contains(completion.Messages, message => message.Code == "test.initialization.failed");
    }

    [Fact]
    public async Task OnceLazyInitializationRunsOnceAcrossWorkers()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<InitializedExecutor>(
                WorkDefinition.Create("lazy.initialized"),
                configure => configure.WithInitialization<RecordingInitializer>(WorkInitializationTiming.OnceLazy)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var first = await (await system.Queue.Enqueue("lazy.initialized")).WaitForCompletion();
        var second = await (await system.Queue.Enqueue("lazy.initialized")).WaitForCompletion();

        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(["initializer", "executor", "executor"], tracker.Events);
    }

    [Fact]
    public async Task InitializersRunInConfiguredOrder()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<InitializedExecutor>(
                WorkDefinition.Create("ordered.initialized"),
                configure => configure
                    .WithInitialization<SecondInitializer>(executionOrder: 20)
                    .WithInitialization<FirstInitializer>(executionOrder: 10)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("ordered.initialized")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(["first", "second", "executor"], tracker.Events);
    }

    [Fact]
    public async Task InitializerAndExecutorUseDifferentScopedServices()
    {
        var tracker = new InitializationTracker();
        var system = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<ScopedMarker>()
            .AddWorkableSystem(builder => builder.AddWork<ScopedExecutor>(
                WorkDefinition.Create("scoped.initialized"),
                configure => configure.WithInitialization<ScopedInitializer>()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("scoped.initialized")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(2, tracker.ScopedMarkers.Count);
        Assert.NotEqual(tracker.ScopedMarkers[0], tracker.ScopedMarkers[1]);
    }

    [Fact]
    public void OnceLazyCannotUseTypedInitializer()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection()
                .AddSingleton<InitializationTracker>()
                .AddWorkableSystem(builder => builder.AddWork<TypedInitializedExecutor>(
                    WorkDefinition.Create("typed.lazy.initialized"),
                    configure => configure.WithInitialization<TypedRecordingInitializer>(WorkInitializationTiming.OnceLazy))));

        Assert.Contains(nameof(WorkInitializationTiming.OnceLazy), exception.Message);
        Assert.Contains("typed initializers depend on worker input", exception.Message);
    }

    private sealed record InitializationInput(string Message);

    private sealed record OtherInitializationInput(string Message);

    private sealed class InitializationTracker
    {
        private readonly List<string> events = [];
        private readonly List<Guid> scopedMarkers = [];

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (this.events)
                {
                    return [.. this.events];
                }
            }
        }

        public IReadOnlyList<Guid> ScopedMarkers
        {
            get
            {
                lock (this.scopedMarkers)
                {
                    return [.. this.scopedMarkers];
                }
            }
        }

        public void Record(string value)
        {
            lock (this.events)
            {
                this.events.Add(value);
            }
        }

        public void RecordScopedMarker(Guid marker)
        {
            lock (this.scopedMarkers)
            {
                this.scopedMarkers.Add(marker);
            }
        }
    }

    private sealed class ScopedMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class RecordingInitializer(InitializationTracker tracker) : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            tracker.Record("initializer");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class TypedRecordingInitializer(InitializationTracker tracker) : IWorkInitializer<InitializationInput>
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            InitializationInput input,
            CancellationToken cancellationToken = default)
        {
            tracker.Record($"initializer:{input.Message}");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class MultiTypedRecordingInitializer(InitializationTracker tracker) :
        IWorkInitializer<InitializationInput>,
        IWorkInitializer<OtherInitializationInput>
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            InitializationInput input,
            CancellationToken cancellationToken = default)
        {
            tracker.Record($"initializer:first:{input.Message}");
            return Task.FromResult(WorkExecutionResult.Success());
        }

        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            OtherInitializationInput input,
            CancellationToken cancellationToken = default)
        {
            tracker.Record($"initializer:other:{input.Message}");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class FailingInitializer(InitializationTracker tracker) : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            tracker.Record("initializer");
            return Task.FromResult(WorkExecutionResult.Failure(
            [
                WorkMessage.Error("test.initialization.failed", "Initialization failed."),
            ]));
        }
    }

    private sealed class FirstInitializer(InitializationTracker tracker) : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            tracker.Record("first");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class SecondInitializer(InitializationTracker tracker) : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            tracker.Record("second");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class ScopedInitializer(
        InitializationTracker tracker,
        ScopedMarker marker) : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            tracker.RecordScopedMarker(marker.Id);
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class InitializedExecutor(InitializationTracker tracker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Record("executor");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class TypedInitializedExecutor(InitializationTracker tracker) : IWorkExecutor<InitializationInput>
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            InitializationInput input,
            CancellationToken cancellationToken)
        {
            tracker.Record($"executor:{input.Message}");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class ScopedExecutor(
        InitializationTracker tracker,
        ScopedMarker marker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.RecordScopedMarker(marker.Id);
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }
}
