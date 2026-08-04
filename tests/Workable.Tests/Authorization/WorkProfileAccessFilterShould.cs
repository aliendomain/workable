using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkProfileAccessFilterShould
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyDiagnosticsVisibilityToEveryProfileBearingOutcome(bool canViewDiagnostics)
    {
        var worker = CreateProfiledWorker();
        var iteration = Assert.Single(worker.Iterations);
        var completion = new WorkCompletion(
            WorkCompletionStatus.Completed,
            worker,
            Output: null,
            Messages: []);
        var typedCompletion = new WorkCompletion<string>(
            WorkCompletionStatus.Completed,
            worker,
            Output: null,
            RawOutput: null,
            Messages: []);
        var action = WorkActionOutcome.Accepted(WorkAction.Cancel, worker);
        var bulk = new WorkerBulkActionOutcome(
            WorkAction.Cancel,
            WorkerBulkActionFilter.All,
            MatchedWorkerCount: 1,
            Outcomes: [action]);
        var stop = new WorkSystemStopResult([worker])
        {
            CancellationRequestedWorkers = [worker],
        };

        var filteredWorker = WorkProfileAccessFilter.Apply(worker, canViewDiagnostics);
        var filteredIteration = WorkProfileAccessFilter.Apply(iteration, canViewDiagnostics);
        var filteredCompletion = WorkProfileAccessFilter.Apply(completion, canViewDiagnostics);
        var filteredTypedCompletion = WorkProfileAccessFilter.Apply(typedCompletion, canViewDiagnostics);
        var filteredAction = WorkProfileAccessFilter.Apply(action, canViewDiagnostics);
        var filteredBulk = WorkProfileAccessFilter.Apply(bulk, canViewDiagnostics);
        var filteredStop = WorkProfileAccessFilter.Apply(stop, canViewDiagnostics);

        AssertProfileVisibility(filteredWorker, canViewDiagnostics);
        Assert.Equal(canViewDiagnostics, filteredIteration.Profile is not null);
        AssertProfileVisibility(filteredCompletion.Worker!, canViewDiagnostics);
        AssertProfileVisibility(filteredTypedCompletion.Worker!, canViewDiagnostics);
        AssertProfileVisibility(filteredAction.Worker!, canViewDiagnostics);
        AssertProfileVisibility(Assert.Single(filteredBulk.Outcomes).Worker!, canViewDiagnostics);
        AssertProfileVisibility(Assert.Single(filteredStop.ForceInterruptedWorkers), canViewDiagnostics);
        AssertProfileVisibility(Assert.Single(filteredStop.CancellationRequestedWorkers), canViewDiagnostics);
        AssertProfileVisibility(worker, expected: true);
    }

    private static WorkerSnapshot CreateProfiledWorker()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new WorkProfile("sensitive profile").ToSnapshot();
        var iteration = new WorkerIterationSnapshot(
            Sequence: 1,
            StartedAt: now,
            CompletedAt: now,
            ExecutionDuration: TimeSpan.Zero,
            Status: WorkCompletionStatus.Completed,
            Output: null,
            Messages: [])
        {
            Profile = profile,
        };
        return new WorkerSnapshot(
            WorkerId.New(),
            Revision: 1,
            StateSequence: 1,
            DefinitionName: "profile.authorization",
            DefinitionCategory: "Tests",
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            RequestContext: WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            State: WorkerState.Completed,
            Input: null,
            Output: null,
            Options: WorkerOptions.Default,
            Configuration: WorkConfiguration.Default,
            Messages: [],
            InterruptionReason: null,
            CreatedAt: now,
            StateChangedAt: now,
            UpdatedAt: now)
        {
            Profile = profile,
            Iterations = [iteration],
            CurrentIteration = iteration,
            LastIteration = iteration,
        };
    }

    private static void AssertProfileVisibility(WorkerSnapshot worker, bool expected)
    {
        Assert.Equal(expected, worker.Profile is not null);
        Assert.Equal(expected, Assert.Single(worker.Iterations).Profile is not null);
        Assert.Equal(expected, worker.CurrentIteration?.Profile is not null);
        Assert.Equal(expected, worker.LastIteration?.Profile is not null);
    }
}
