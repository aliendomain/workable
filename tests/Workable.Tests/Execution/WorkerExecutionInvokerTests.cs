using Microsoft.Extensions.DependencyInjection;

namespace Workable.Tests;

[Trait("Category", "Execution")]
public sealed class WorkerExecutionInvokerTests
{
    [Fact]
    public async Task ExecuteHonorsCanceledTokenBeforeCreatingExecutionScope()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        await provider.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var invoker = CreateInvoker(provider);
        var worker = CreateWorker(initializers: []);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await invoker.Execute(worker, cancellation.Token));
    }

    [Fact]
    public async Task ExecuteHonorsCanceledTokenBeforeCreatingInitializationScope()
    {
        var provider = new ServiceCollection()
            .AddTransient<ShouldNotResolveInitializer>()
            .BuildServiceProvider();
        await provider.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var invoker = CreateInvoker(provider);
        var worker = CreateWorker(
            [
                WorkInitializationRegistration.Create<ShouldNotResolveInitializer>(
                    WorkInitializationTiming.OncePerWorker,
                    executionOrder: null),
            ]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await invoker.Execute(worker, cancellation.Token));
    }

    private static WorkerExecutionInvoker CreateInvoker(IServiceProvider services)
    {
        var systemId = WorkSystemId.New();
        var events = new WorkEventStream();
        var publisher = new WorkerEventPublisher(systemId, events, synchronize: _ => { });
        return new WorkerExecutionInvoker(
            systemId,
            workSystemName: null,
            services,
            publisher,
            identifierDiscovered: (_, _) => { },
            new WorkInitializationExecutor(services));
    }

    private static WorkerRecord CreateWorker(IReadOnlyList<WorkInitializationRegistration> initializers)
    {
        var work = new RegisteredWork(
            WorkDefinition.Create("execution.canceled-before-scope"),
            _ => new ShouldNotExecuteWork(),
            ExceptionClassifiers: [],
            AutomaticStarts: [],
            Initializers: initializers);

        return new WorkerRecord(
            WorkerId.New(),
            work,
            WorkInput.Empty,
            WorkerOptions.Default,
            WorkConfiguration.Default,
            WorkOrigin.Create(WorkInvocationChannel.DotNet),
            WorkerState.Running,
            isStartDeferred: false,
            messages: [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private sealed class ShouldNotResolveInitializer : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(IWorkExecutionContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Initializer should not run when execution is already canceled.");
    }

    private sealed class ShouldNotExecuteWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("Worker should not run when execution is already canceled.");
    }
}
