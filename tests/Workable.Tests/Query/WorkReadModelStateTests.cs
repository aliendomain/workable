using Workable;

namespace Workable.Tests;

[Trait("Category", "Query")]
public sealed class WorkReadModelStateTests
{
    [Fact]
    public void ReadModelStateIgnoresOlderWorkerUpdates()
    {
        var state = new WorkSystemReadModelState();
        var workerId = WorkerId.New();
        var definitionId = WorkDefinitionId.New();
        var createdAt = DateTimeOffset.UtcNow;
        var queued = CreateWorker(workerId, definitionId, "readmodel.sequence", WorkerState.Queued, createdAt);
        var running = queued with
        {
            Overview = queued.Overview with
            {
                State = WorkerState.Running,
                StateChangedAt = createdAt.AddSeconds(1),
                UpdatedAt = createdAt.AddSeconds(1),
            },
        };

        state.RecordWorker(running, sequence: 2);
        state.RecordWorker(queued, sequence: 1);

        var snapshot = state.ToSnapshot();
        var current = Assert.Single(snapshot.Workers);
        Assert.Equal(workerId, current.Id);
        Assert.Equal(WorkerState.Running, current.State);
        Assert.Equal(workerId, Assert.Single(snapshot.WorkersByState[WorkerState.Running]).Id);
        Assert.False(snapshot.WorkersByState.ContainsKey(WorkerState.Queued));
    }

    [Fact]
    public void ReadModelStateClearPreventsOlderUpdatesFromResurrectingRows()
    {
        var state = new WorkSystemReadModelState();
        var worker = CreateWorker(
            WorkerId.New(),
            WorkDefinitionId.New(),
            "readmodel.clear.sequence",
            WorkerState.Queued,
            DateTimeOffset.UtcNow);

        state.RecordWorker(worker, sequence: 1);
        state.Clear(sequence: 10);
        state.RecordWorker(worker, sequence: 9);

        Assert.Empty(state.ToSnapshot().Workers);

        state.RecordWorker(worker, sequence: 11);

        Assert.Equal(worker.Id, Assert.Single(state.ToSnapshot().Workers).Id);
    }

    [Fact]
    public void ReadModelStateForgetWorkerRemovesWorkerIterationsAndKeys()
    {
        var state = new WorkSystemReadModelState();
        var workerId = WorkerId.New();
        var definitionId = WorkDefinitionId.New();
        var identifier = new WorkIdentifier("claim", "CLM-1");
        var worker = CreateWorker(
            workerId,
            definitionId,
            "readmodel.forget.worker",
            WorkerState.Completed,
            DateTimeOffset.UtcNow,
            subjectId: new WorkSubjectId("claim", "CLM-1"),
            concurrencyKey: new WorkConcurrencyKey("tenant", "west"),
            identifiers: new HashSet<WorkIdentifier> { identifier });
        var iteration = CreateIteration(worker, sequence: 1, WorkCompletionStatus.Completed);

        state.RecordIteration(new WorkerReadModelIterationUpdate(worker, iteration, null!), sequence: 1);
        state.ForgetWorker(workerId, sequence: 2);

        var snapshot = state.ToSnapshot();
        Assert.Empty(snapshot.Workers);
        Assert.Empty(snapshot.Iterations);
        Assert.Empty(snapshot.WorkerKeys);
        Assert.Empty(snapshot.IterationKeys);
        Assert.False(snapshot.WorkersByIdentifier.ContainsKey(identifier));
        Assert.False(snapshot.IterationsByIdentifier.ContainsKey(identifier));
        Assert.False(snapshot.IterationsByWorker.ContainsKey(workerId));
    }

    [Fact]
    public void ReadModelStateForgetWorkersRemovesMultipleWorkersAndLeavesOthers()
    {
        var state = new WorkSystemReadModelState();
        var definitionId = WorkDefinitionId.New();
        var first = CreateWorker(WorkerId.New(), definitionId, "readmodel.forget.batch", WorkerState.Completed, DateTimeOffset.UtcNow);
        var second = CreateWorker(WorkerId.New(), definitionId, "readmodel.forget.batch", WorkerState.Completed, DateTimeOffset.UtcNow.AddSeconds(1));
        var retained = CreateWorker(WorkerId.New(), definitionId, "readmodel.forget.batch", WorkerState.Completed, DateTimeOffset.UtcNow.AddSeconds(2));

        state.RecordIteration(new WorkerReadModelIterationUpdate(first, CreateIteration(first, 1, WorkCompletionStatus.Completed), null!), sequence: 1);
        state.RecordIteration(new WorkerReadModelIterationUpdate(second, CreateIteration(second, 1, WorkCompletionStatus.Completed), null!), sequence: 2);
        state.RecordIteration(new WorkerReadModelIterationUpdate(retained, CreateIteration(retained, 1, WorkCompletionStatus.Completed), null!), sequence: 3);

        state.ForgetWorkers([first.Id, second.Id], sequence: 4);

        var snapshot = state.ToSnapshot();
        Assert.Equal(retained.Id, Assert.Single(snapshot.Workers).Id);
        Assert.Equal(retained.Id, Assert.Single(snapshot.Iterations).WorkerId);
        Assert.False(snapshot.WorkersById.ContainsKey(first.Id));
        Assert.False(snapshot.WorkersById.ContainsKey(second.Id));
        Assert.False(snapshot.IterationsByWorker.ContainsKey(first.Id));
        Assert.False(snapshot.IterationsByWorker.ContainsKey(second.Id));
    }

    private static WorkerReadModelWorker CreateWorker(
        WorkerId workerId,
        WorkDefinitionId definitionId,
        string definitionName,
        WorkerState state,
        DateTimeOffset timestamp,
        WorkSubjectId? subjectId = null,
        WorkConcurrencyKey? concurrencyKey = null,
        IReadOnlySet<WorkIdentifier>? identifiers = null)
        => WorkerReadModelWorker.From(
            new WorkerOverviewItem(
                workerId,
                definitionId,
                definitionName,
                subjectId,
                concurrencyKey,
                identifiers ?? new HashSet<WorkIdentifier>(),
                Revision: 1,
                Category: "ReadModel",
                state,
                InterruptionReason: null,
                CreatedAt: timestamp,
                StateChangedAt: timestamp,
                UpdatedAt: timestamp),
            recurrenceEnabled: false,
            concurrencyEnabled: false,
            profilingEnabled: false);

    private static WorkerReadModelIteration CreateIteration(
        WorkerReadModelWorker worker,
        long sequence,
        WorkCompletionStatus status)
        => new(
            new WorkerIterationReference(worker.Id, sequence),
            new WorkerIterationOverviewItem(
                worker.Id,
                sequence,
                worker.DefinitionId,
                worker.DefinitionName,
                worker.Category,
                worker.State,
                status,
                StartedAt: worker.UpdatedAt,
                CompletedAt: worker.UpdatedAt,
                ExecutionDuration: TimeSpan.Zero,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers.ToArray()));
}
