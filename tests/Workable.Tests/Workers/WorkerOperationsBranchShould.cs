using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workers")]
public sealed class WorkerOperationsBranchShould
{
    [Fact]
    public async Task WorkerCompletionContinuationPropagatesSuccessCancellationAndFailure()
    {
        var expected = new WorkCompletion(WorkCompletionStatus.Completed, null, null, []);
        var succeeded = new TaskCompletionSource<WorkCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeWorkerRecordStatic("CompleteWhenExecutionFinishes", Task.FromResult(expected), succeeded);
        Assert.Same(expected, await succeeded.Task);

        using var canceledSource = new CancellationTokenSource();
        canceledSource.Cancel();
        var canceled = new TaskCompletionSource<WorkCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeWorkerRecordStatic(
            "CompleteWhenExecutionFinishes",
            Task.FromCanceled<WorkCompletion>(canceledSource.Token),
            canceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.Task);

        var failed = new TaskCompletionSource<WorkCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeWorkerRecordStatic(
            "CompleteWhenExecutionFinishes",
            Task.FromException<WorkCompletion>(new InvalidOperationException("execution failed")),
            failed);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.Task);
        Assert.Equal("execution failed", exception.Message);
    }

    [Fact]
    public void WorkerRecordInterruptionAndTransitionValidationCoversAllBoundaries()
    {
        foreach (var state in Enum.GetValues<WorkerState>())
        foreach (var reason in Enum.GetValues<WorkInterruptionReason>())
        {
            var expected = reason == WorkInterruptionReason.LeaseLost
                ? state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying or WorkerState.Paused
                : state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying;
            Assert.Equal(expected, InvokeWorkerRecordStatic<bool>("CanInterrupt", state, reason));
        }

        Assert.False(InvokeWorkerRecordStatic<bool>(
            "CanInterrupt",
            (WorkerState)int.MaxValue,
            WorkInterruptionReason.LeaseLost));
        var missingCode = new WorkerStateTransition(
            WorkAction.Start,
            WorkerState.Paused,
            WorkActionStatus.Invalid,
            null,
            false,
            false,
            null,
            "message");
        var missingText = missingCode with { MessageCode = "code", MessageText = null };
        Assert.Throws<TargetInvocationException>(() =>
            InvokeWorkerRecordStatic<object>("ToMessage", missingCode));
        Assert.Throws<TargetInvocationException>(() =>
            InvokeWorkerRecordStatic<object>("ToMessage", missingText));
    }

    [Fact]
    public void ClassifyEveryCriticalRetentionFailure()
    {
        var critical = new Exception[]
        {
            new OutOfMemoryException(),
            new StackOverflowException(),
            new AccessViolationException(),
            new AppDomainUnloadedException(),
            new BadImageFormatException(),
            new CannotUnloadAppDomainException(),
            (Exception)RuntimeHelpers.GetUninitializedObject(typeof(ThreadAbortException)),
            new InvalidProgramException(),
        };

        Assert.All(critical, exception =>
            Assert.False(InvokeStatic<bool>("IsNonCriticalRetentionFailure", exception)));
        Assert.True(InvokeStatic<bool>("IsNonCriticalRetentionFailure", new InvalidOperationException()));
    }

    [Fact]
    public void SelectWorkersFromListOrHashSetAndClassifyStopStates()
    {
        var first = WorkerId.New();
        var second = WorkerId.New();
        var workers = new List<WorkerId> { first };

        Assert.True(InvokeStatic<bool>("ContainsWorker", workers, null, first));
        Assert.False(InvokeStatic<bool>("ContainsWorker", workers, null, second));
        Assert.True(InvokeStatic<bool>("ContainsWorker", workers, new HashSet<WorkerId> { second }, second));
        Assert.False(InvokeStatic<bool>("ContainsWorker", workers, new HashSet<WorkerId> { second }, first));

        foreach (var state in Enum.GetValues<WorkerState>())
        {
            var expected = state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying;
            Assert.Equal(expected, InvokeStatic<bool>("ShouldInterruptForSystemStop", state));
        }
    }

    [Fact]
    public void ForcedProfileCapturePreservesOptionsAndSetsRequestedMode()
    {
        var fromDefaults = InvokeStatic<WorkerOptions>(
            "ForceProfileCapture",
            null,
            WorkProfileCaptureMode.Full);
        var bounded = InvokeStatic<WorkerOptions>(
            "ForceProfileCapture",
            new WorkerOptions(ProfilingEnabled: false),
            WorkProfileCaptureMode.Bounded);

        Assert.True(fromDefaults.ProfilingEnabled);
        Assert.Equal(WorkProfileCaptureMode.Full, fromDefaults.ProfilingCaptureMode);
        Assert.True(bounded.ProfilingEnabled);
        Assert.Equal(WorkProfileCaptureMode.Bounded, bounded.ProfilingCaptureMode);
    }

    [Fact]
    public void CapacityBucketsCountForEveryBlockingMode()
    {
        foreach (var mode in Enum.GetValues<WorkConcurrencyBlockingMode>())
        {
            Assert.True(WorkConcurrencyCapacityBucket.Executing.CountsFor(mode));
            Assert.Equal(
                mode is WorkConcurrencyBlockingMode.WhileExecutingOrPaused or
                    WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
                WorkConcurrencyCapacityBucket.Paused.CountsFor(mode));
            Assert.Equal(
                mode is WorkConcurrencyBlockingMode.WhileExecutingOrFailed or
                    WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
                WorkConcurrencyCapacityBucket.Failed.CountsFor(mode));
        }

        Assert.False(((WorkConcurrencyCapacityBucket)99).CountsFor(WorkConcurrencyBlockingMode.WhileExecuting));
    }

    [Fact]
    public async Task PreserveMultipleExecutionFailuresWhileUnwrappingASingleFailure()
    {
        var first = new InvalidOperationException("first");
        var second = new NotSupportedException("second");
        var singleTask = Task.FromException(first);
        var multipleTask = Task.WhenAll(Task.FromException(first), Task.FromException(second));
        await Assert.ThrowsAnyAsync<Exception>(() => multipleTask);
        var method = typeof(WorkerExecutionAttemptRunner).GetMethod(
            "GetExecutionException",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var single = Assert.IsType<InvalidOperationException>(method.Invoke(null, [singleTask]));
        var multiple = Assert.IsType<AggregateException>(method.Invoke(null, [multipleTask]));

        Assert.Same(first, single);
        Assert.Equal(2, multiple.InnerExceptions.Count);
    }

    [Fact]
    public async Task LeaseLossForgetsFailedWorkersAndOrdinaryInterruptionsRespectWorkerState()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("worker.operations.lease-loss"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                    [WorkMessage.Error("test.failure", "Expected failure.")]))))
            .BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(
            provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();
        var session = await system.CreateSession(
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var handle = await session.Queue.Enqueue("worker.operations.lease-loss", WorkInput.Empty);
        Assert.Equal(WorkCompletionStatus.Failed, (await handle.WaitForCompletion()).Status);

        var failed = GetTrackedWorker(system.WorkerOperations, handle.WorkerId!.Value);
        InvokeInstance(system.WorkerOperations, "InterruptWorker", failed, WorkInterruptionReason.LeaseLost);
        Assert.Null(TryGetTrackedWorker(system.WorkerOperations, failed.Id));

        // A duplicate lease-loss notification is harmless once ownership has already been forgotten.
        InvokeInstance(system.WorkerOperations, "InterruptWorker", failed, WorkInterruptionReason.LeaseLost);
        InvokeInstance(system.WorkerOperations, "InterruptWorker", failed, WorkInterruptionReason.Shutdown);

        var queued = CreateQueuedWorker("worker.operations.interrupt.queued");
        InvokeInstance(system.WorkerOperations, "InterruptWorker", queued, WorkInterruptionReason.Shutdown);
        Assert.Equal(WorkerState.Interrupted, queued.State);
    }

    [Fact]
    public async Task ExerciseBulkSelectionCapacityAndRetentionBoundaryInputs()
    {
        var deferredConfiguration = WorkConfiguration.Default with
        {
            Start = WorkStartConfiguration.DoNotStart,
        };
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("worker.operations.bulk.one", category: "Operations"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWork(
                    WorkDefinition.Create(
                        "worker.operations.bulk.two",
                        category: "Billing",
                        configuration: deferredConfiguration),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            })
            .BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(
            provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();
        var session = await system.CreateSession(
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completedHandle = await session.Queue.Enqueue("worker.operations.bulk.one", WorkInput.Empty);
        Assert.Equal(WorkCompletionStatus.Completed, (await completedHandle.WaitForCompletion()).Status);
        var deferredHandle = await session.Queue.Enqueue("worker.operations.bulk.two", WorkInput.Empty);
        var completed = GetTrackedWorker(system.WorkerOperations, completedHandle.WorkerId!.Value);
        var deferred = GetTrackedWorker(system.WorkerOperations, deferredHandle.WorkerId!.Value);
        Assert.Equal(WorkerState.Queued, deferred.State);

        var definitions = system.Catalog.Definitions.ToArray();
        var allIds = definitions.Select(definition => definition.Id).ToHashSet();
        Assert.Empty(GetBulkCandidates(system.WorkerOperations, WorkerBulkActionFilter.All, new HashSet<WorkDefinitionId>()));
        Assert.Equal(2, GetBulkCandidates(system.WorkerOperations, WorkerBulkActionFilter.All, allIds).Count);
        Assert.Single(GetBulkCandidates(
            system.WorkerOperations,
            new WorkerBulkActionFilter("Operations", IncludeSubcategories: false),
            null));
        Assert.Empty(GetBulkCandidates(
            system.WorkerOperations,
            new WorkerBulkActionFilter("Missing"),
            allIds));

        var acceptingWork = typeof(WorkerOperations).GetField(
            "acceptingWork",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        acceptingWork.SetValue(system.WorkerOperations, false);
        try
        {
            object?[] acceptArguments = [null];
            var accepted = (bool)typeof(WorkerOperations).GetMethod(
                "TryAcceptWork",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
                    system.WorkerOperations,
                    acceptArguments)!;
            Assert.False(accepted);
            Assert.NotNull(acceptArguments[0]);
        }
        finally
        {
            acceptingWork.SetValue(system.WorkerOperations, true);
        }

        Assert.Equal(0, InvokeInstance<int>(
            system.WorkerOperations,
            "AutoCancelFailedWorkers",
            Array.Empty<FailedWorkerAutoCancelSchedule>()));
        Assert.Equal(0, InvokeInstance<int>(
            system.WorkerOperations,
            "PurgeFinalWorkersForRetention",
            Array.Empty<WorkerId>(),
            null));
        Assert.Equal(0, InvokeInstance<int>(
            system.WorkerOperations,
            "PurgeFinalWorkersForRetention",
            new[] { WorkerId.New(), deferred.Id, completed.Id },
            WorkDefinitionId.New()));

        Assert.False(InvokeInstance<bool>(system.WorkerOperations, "ShouldKeepWorkflowChildWorker", deferred));
        Assert.False(InvokeInstance<bool>(system.WorkerOperations, "ShouldKeepWorkflowChildWorker", completed));
        Assert.False(InvokeInstance<bool>(system.WorkerOperations, "ShouldRetryWorkflowChildFinalization", deferred));
        Assert.False(InvokeInstance<bool>(system.WorkerOperations, "ShouldRetryWorkflowChildFinalization", completed));
        InvokeInstance(system.WorkerOperations, "ForgetIterationStatuses", new List<WorkerId>());
        InvokeInstance(
            system.WorkerOperations,
            "ForgetIterationStatuses",
            Enumerable.Range(0, 5).Select(_ => WorkerId.New()).ToList());
    }

    private static T InvokeStatic<T>(string name, params object?[] arguments)
        => (T)typeof(WorkerOperations)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments)!;

    private static void InvokeInstance(object target, string name, params object?[] arguments)
        => target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => MethodMatches(method, name, arguments))
            .Invoke(target, arguments);

    private static T InvokeInstance<T>(object target, string name, params object?[] arguments)
        => (T)target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => MethodMatches(method, name, arguments))
            .Invoke(target, arguments)!;

    private static bool MethodMatches(MethodInfo method, string name, object?[] arguments)
        => method.Name == name &&
            method.GetParameters().Length == arguments.Length &&
            method.GetParameters()
                .Zip(arguments)
                .All(pair => pair.Second is null ||
                    pair.First.ParameterType.IsInstanceOfType(pair.Second) ||
                    Nullable.GetUnderlyingType(pair.First.ParameterType) == pair.Second.GetType());

    private static IReadOnlyList<WorkerRecord> GetBulkCandidates(
        WorkerOperations operations,
        WorkerBulkActionFilter filter,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
        => InvokeInstance<IReadOnlyList<WorkerRecord>>(
            operations,
            "GetBulkActionCandidateRecords",
            filter,
            definitionIds);

    private static WorkerRecord GetTrackedWorker(WorkerOperations operations, WorkerId workerId)
        => TryGetTrackedWorker(operations, workerId)
            ?? throw new InvalidOperationException("Expected a tracked worker.");

    private static WorkerRecord? TryGetTrackedWorker(WorkerOperations operations, WorkerId workerId)
        => (WorkerRecord?)operations.GetType()
            .GetMethod("GetTrackedWorker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(operations, [workerId]);

    private static WorkerRecord CreateQueuedWorker(string definitionName)
    {
        var definition = WorkDefinition.Create(definitionName);
        var now = DateTimeOffset.UtcNow;
        return new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.Empty,
            WorkerOptions.Default,
            definition.Configuration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: now,
            updatedAt: now);
    }

    private static void InvokeWorkerRecordStatic(string name, params object?[] arguments)
        => typeof(WorkerRecord)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments);

    private static T InvokeWorkerRecordStatic<T>(string name, params object?[] arguments)
        => (T)typeof(WorkerRecord)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments)!;

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
