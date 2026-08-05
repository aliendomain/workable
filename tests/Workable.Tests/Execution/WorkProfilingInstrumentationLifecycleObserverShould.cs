using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkProfilingInstrumentationLifecycleObserverShould
{
    [Fact]
    public async Task CreateAndDisposeEveryRegisteredInstrumentationForTheSystemLifecycle()
    {
        var first = new RecordingFactory();
        var second = new RecordingFactory();
        using var provider = new ServiceCollection()
            .AddSingleton<IWorkProfilingInstrumentationFactory>(first)
            .AddSingleton<IWorkProfilingInstrumentationFactory>(second)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("instrumentation.lifecycle", "Exercises instrumentation lifecycle."),
                SuccessfulWork))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        Assert.Equal([system.Id], first.CreatedSystemIds);
        Assert.Equal([system.Id], second.CreatedSystemIds);
        Assert.False(first.Handles.Single().IsDisposed);
        Assert.False(second.Handles.Single().IsDisposed);

        await system.Stop();

        Assert.True(first.Handles.Single().IsDisposed);
        Assert.True(second.Handles.Single().IsDisposed);
    }

    [Fact]
    public async Task DisposeAlreadyCreatedInstrumentationWhenALaterFactoryFails()
    {
        var successful = new RecordingFactory();
        using var provider = new ServiceCollection()
            .AddSingleton<IWorkProfilingInstrumentationFactory>(successful)
            .AddSingleton<IWorkProfilingInstrumentationFactory>(new ThrowingFactory())
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("instrumentation.failure", "Exercises instrumentation startup failure."),
                SuccessfulWork))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal("instrumentation factory failed", exception.Message);
        Assert.True(successful.Handles.Single().IsDisposed);
    }

    [Fact]
    public async Task ValidateLifecycleArgumentsAndIgnoreUnknownStoppedSystems()
    {
        var factory = new RecordingFactory();
        var observer = new WorkProfilingInstrumentationLifecycleObserver(
            [factory],
            new WorkProfilingContextAccessor());
        using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("instrumentation.arguments", "Exercises instrumentation arguments."),
                SuccessfulWork))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(() => observer.SystemStarted(null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.SystemStarted(system, canceled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => observer.SystemStopped(null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.SystemStopped(system, canceled.Token));
        await observer.SystemStopped(system);
        await observer.SystemStarted(system);
        await observer.SystemStarted(system);
        Assert.Single(factory.Handles);

        observer.Dispose();
        observer.Dispose();
        Assert.True(factory.Handles.Single().IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => observer.SystemStarted(system));
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed class RecordingFactory : IWorkProfilingInstrumentationFactory
    {
        public List<WorkSystemId> CreatedSystemIds { get; } = [];

        public List<RecordingHandle> Handles { get; } = [];

        public IDisposable Create(
            WorkSystemId systemId,
            IWorkProfilingContextAccessor profilingContextAccessor)
        {
            this.CreatedSystemIds.Add(systemId);
            var handle = new RecordingHandle();
            this.Handles.Add(handle);
            return handle;
        }
    }

    private sealed class ThrowingFactory : IWorkProfilingInstrumentationFactory
    {
        public IDisposable Create(
            WorkSystemId systemId,
            IWorkProfilingContextAccessor profilingContextAccessor)
            => throw new InvalidOperationException("instrumentation factory failed");
    }

    private sealed class RecordingHandle : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
            => this.IsDisposed = true;
    }
}
