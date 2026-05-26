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

    [Fact]
    public async Task ExecuteReturnsFailureWhenExecutionContextIsMarkedFailed()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var invoker = CreateInvoker(provider);
        var worker = CreateWorker(initializers: [], executorFactory: _ => new ContextFailingWork());

        var invocation = await invoker.Execute(worker, CancellationToken.None);
        var result = invocation.Result;

        Assert.True(result.HasErrors);
        var message = Assert.Single(result.Messages);
        Assert.Equal("test.context.failed", message.Code);
        Assert.Equal(WorkMessageSeverity.Error, message.Severity);
        Assert.Equal("The execution context marked this work as failed.", message.Text);
        Assert.Equal("execution", message.Target);
        Assert.Equal("executionContext", message.Metadata?["failureSource"]);
        Assert.False(invocation.RequestedFailureIsTransient);
    }

    [Fact]
    public async Task ExecutePreservesExistingExecutorMessagesAlongsideRequestedContextFailure()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var invoker = CreateInvoker(provider);
        var worker = CreateWorker(initializers: [], executorFactory: _ => new ContextFailingWorkWithAdditionalMessage());

        var invocation = await invoker.Execute(worker, CancellationToken.None);
        var result = invocation.Result;

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("test.context.failed", result.Messages[0].Code);
        Assert.Equal("test.context.additional", result.Messages[1].Code);
    }

    private static WorkerExecutionInvoker CreateInvoker(IServiceProvider services)
    {
        var systemId = WorkSystemId.New();
        var events = new WorkEventStream();
        var publisher = new WorkerEventPublisher(systemId, null, events, synchronize: _ => { });
        return new WorkerExecutionInvoker(
            systemId,
            workSystemName: null,
            services,
            new NoOpWorkerPersistenceCoordinator(),
            publisher,
            identifierDiscovered: (_, _) => { },
            new WorkInitializationExecutor(services));
    }

    private static WorkerRecord CreateWorker(
        IReadOnlyList<WorkInitializationRegistration> initializers,
        Func<IServiceProvider, IWorkExecutor>? executorFactory = null)
    {
        var work = new RegisteredWork(
            WorkDefinition.Create("execution.canceled-before-scope"),
            executorFactory ?? (_ => new ShouldNotExecuteWork()),
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

    private sealed class NoOpWorkerPersistenceCoordinator : IWorkerPersistenceCoordinator
    {
        public Task InitializeAndDrain(IReadOnlyList<WorkDefinition> definitions, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void StartBackgroundTasks()
        {
        }

        public Task StopBackgroundTasks(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<WorkerPersistenceQueueAcceptance> AcceptQueuedWorker(
            WorkerId workerId,
            RegisteredWork registeredWork,
            WorkInput? input,
            RegisteredWorkRuntimePlan runtimePlan,
            WorkOrigin origin,
            DateTimeOffset now,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void SignalAccepted(WorkerRecord worker)
        {
        }

        public void SynchronizeWorkerState(WorkerRecord worker)
        {
        }

        public Task CompleteDurably(
            WorkerRecord worker,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkSubjectId subjectId)
            => [];

        public IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkDefinitionId definitionId, WorkSubjectId subjectId)
            => [];
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

    private sealed class ContextFailingWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            context.Fail("test.context.failed", "The execution context marked this work as failed.", "execution");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class ContextFailingWorkWithAdditionalMessage : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            context.Fail("test.context.failed", "The execution context marked this work as failed.", "execution");
            return Task.FromResult(WorkExecutionResult.Success(
                messages:
                [
                    WorkMessage.Info("test.context.additional", "The executor also returned a non-error detail message.")
                ]));
        }
    }
}
