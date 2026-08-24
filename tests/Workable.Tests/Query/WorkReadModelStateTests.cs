using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Query")]
public sealed class WorkReadModelStateTests
{
    [Fact]
    public async Task ReadModelInboxCoalescesWorkerIterationBatchAndClearUpdates()
    {
        await using var readModel = new WorkSystemReadModel(
            new WorkSystemCatalog([], persistenceStoreAvailable: false),
            () => WorkSystemState.Started,
            workSystemName: null,
            new InMemoryWorkMetricsSink());
        var definitionId = WorkDefinitionId.New();
        var first = CreateWorker(WorkerId.New(), definitionId, "readmodel.pending.first", WorkerState.Running, DateTimeOffset.UtcNow);
        var second = CreateWorker(WorkerId.New(), definitionId, "readmodel.pending.second", WorkerState.Running, DateTimeOffset.UtcNow);
        var firstIteration = new WorkerReadModelIterationUpdate(first, CreateIteration(first, 1, WorkCompletionStatus.Executing), null!);
        var secondIteration = new WorkerReadModelIterationUpdate(second, CreateIteration(second, 1, WorkCompletionStatus.Executing), null!);

        Store("RecordWorkerUpdate", 1L, first);
        Store("RecordIterationUpdate", 2L, firstIteration);
        Store("RecordWorkerUpdate", 3L, second);
        Store("RecordIterationUpdate", 4L, secondIteration);
        Store("ForgetWorkerUpdate", 5L, first.Id);
        Store("ForgetWorkersUpdate", 6L, new[]
        {
            second.Id,
            WorkerId.New(),
            WorkerId.New(),
            WorkerId.New(),
            WorkerId.New(),
        });
        var largeBatch = TakeBatch();
        Assert.Equal(6L, ReadBatchUpdateCount(largeBatch));

        Store("RecordWorkerUpdate", 7L, first);
        Store("RecordIterationUpdate", 8L, firstIteration);
        Store("RecordWorkerUpdate", 9L, second);
        Store("RecordIterationUpdate", 10L, secondIteration);
        Store("ForgetWorkersUpdate", 11L, new[] { first.Id, second.Id });
        Store("ClearReadModelUpdate", 12L);
        var clearedBatch = TakeBatch();
        Assert.Equal(6L, ReadBatchUpdateCount(clearedBatch));
        Assert.Equal(0L, ReadBatchUpdateCount(TakeBatch()));

        var containsWorker = typeof(WorkSystemReadModel).GetMethod(
            "ContainsWorker",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        IReadOnlyCollection<WorkerId> ids = new[] { first.Id };
        Assert.True((bool)containsWorker.Invoke(null, [ids, null, first.Id])!);
        Assert.False((bool)containsWorker.Invoke(null, [ids, null, second.Id])!);
        Assert.True((bool)containsWorker.Invoke(null, [ids, new HashSet<WorkerId> { first.Id }, first.Id])!);
        Assert.False((bool)containsWorker.Invoke(null, [ids, new HashSet<WorkerId> { first.Id }, second.Id])!);

        var removeIndex = typeof(WorkSystemReadModelState)
            .GetMethod("RemoveIndex", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(string), typeof(string));
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["key"] = ["first", "second"],
        };
        removeIndex.Invoke(null, [index, "missing", "first"]);
        removeIndex.Invoke(null, [index, "key", "first"]);
        Assert.Equal(["second"], index["key"]);
        removeIndex.Invoke(null, [index, "key", "second"]);
        Assert.Empty(index);

        void Store(string typeName, params object?[] arguments)
        {
            var update = Activator.CreateInstance(
                typeof(WorkSystemReadModel).GetNestedType(typeName, BindingFlags.NonPublic)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: arguments,
                culture: null)!;
            typeof(WorkSystemReadModel).GetMethod(
                "StorePendingUpdateLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(readModel, [update]);
        }

        object TakeBatch()
            => typeof(WorkSystemReadModel).GetMethod(
                "TakePendingUpdates",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(readModel, null)!;

        static long ReadBatchUpdateCount(object batch)
            => (long)batch.GetType().GetProperty("UpdateCount")!.GetValue(batch)!;
    }

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

    [Fact]
    public void ReadModelStateRejectsStaleIterationForgetAndClearSequences()
    {
        var state = new WorkSystemReadModelState();
        var worker = CreateWorker(
            WorkerId.New(),
            WorkDefinitionId.New(),
            "readmodel.iteration.sequence",
            WorkerState.Completed,
            DateTimeOffset.UtcNow);
        var original = CreateIteration(worker, 1, WorkCompletionStatus.Completed);
        var replacement = new WorkerReadModelIteration(
            worker.DefinitionId,
            original.Reference,
            original.Overview with { Status = WorkCompletionStatus.Failed });

        state.RecordIteration(new WorkerReadModelIterationUpdate(worker, original, null!), sequence: 5);
        state.RecordIteration(new WorkerReadModelIterationUpdate(worker, replacement, null!), sequence: 4);
        Assert.Equal(WorkCompletionStatus.Completed, Assert.Single(state.ToSnapshot().Iterations).Status);

        state.ForgetIteration(original.Reference, sequence: 4);
        Assert.Single(state.ToSnapshot().Iterations);
        state.ForgetIteration(original.Reference, sequence: 6);
        state.ForgetIteration(original.Reference, sequence: 7);
        Assert.Empty(state.ToSnapshot().Iterations);

        state.Clear(sequence: 10);
        state.RecordIteration(new WorkerReadModelIterationUpdate(worker, original, null!), sequence: 9);
        state.ForgetIteration(original.Reference, sequence: 9);
        state.ForgetWorker(worker.Id, sequence: 9);
        state.Clear(sequence: 9);
        Assert.Empty(state.ToSnapshot().Workers);
    }

    [Fact]
    public void ReadModelStatePreservesIterationsNewerThanAWorkerForgetSequence()
    {
        var state = new WorkSystemReadModelState();
        var worker = CreateWorker(
            WorkerId.New(),
            WorkDefinitionId.New(),
            "readmodel.forget.newer-iteration",
            WorkerState.Completed,
            DateTimeOffset.UtcNow);
        var iteration = CreateIteration(worker, 1, WorkCompletionStatus.Completed);
        state.RecordIteration(new WorkerReadModelIterationUpdate(worker, iteration, null!), sequence: 10);

        var workerSequences = (Dictionary<WorkerId, long>)typeof(WorkSystemReadModelState)
            .GetField("workerSequences", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;
        workerSequences[worker.Id] = 1;

        state.ForgetWorker(worker.Id, sequence: 5);

        var snapshot = state.ToSnapshot();
        Assert.Empty(snapshot.Workers);
        Assert.Equal(iteration.Reference, Assert.Single(snapshot.Iterations).Reference);
    }

    [Fact]
    public void ReadModelStateReplacesRowsAndRemovesEveryOptionalIndexShape()
    {
        var state = new WorkSystemReadModelState();
        var worker = CreateWorker(
            WorkerId.New(),
            WorkDefinitionId.New(),
            "readmodel.replace",
            WorkerState.Running,
            DateTimeOffset.UtcNow,
            new WorkSubjectId("subject", "1"),
            new WorkConcurrencyKey("tenant", "west"),
            new HashSet<WorkIdentifier> { new("order", "2") });
        var first = CreateIteration(worker, 1, WorkCompletionStatus.Executing);
        var changedWorker = worker with
        {
            Overview = worker.Overview with
            {
                State = WorkerState.Completed,
                SubjectId = null,
                ConcurrencyKey = null,
                Identifiers = new HashSet<WorkIdentifier>(),
            },
            OriginActorId = "actor",
        };
        var completed = new WorkerReadModelIteration(
            changedWorker.DefinitionId,
            first.Reference,
            first.Overview with
            {
                WorkerState = WorkerState.Completed,
                Status = WorkCompletionStatus.Completed,
                SubjectId = null,
                ConcurrencyKey = null,
                Identifiers = [],
            });

        state.RecordIteration(new WorkerReadModelIterationUpdate(worker, first, null!), sequence: 1);
        state.RecordIteration(new WorkerReadModelIterationUpdate(changedWorker, completed, null!), sequence: 2);
        state.ForgetWorker(changedWorker.Id, sequence: 3);

        Assert.Empty(state.ToSnapshot().Workers);
        Assert.Empty(state.ToSnapshot().Iterations);
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
            definitionId,
            new WorkerOverviewItem(
                workerId,
                definitionName,
                subjectId,
                concurrencyKey,
                identifiers ?? new HashSet<WorkIdentifier>(),
                1,
                "ReadModel",
                state,
                null,
                timestamp,
                timestamp,
                timestamp),
            recurrenceEnabled: false,
            concurrencyEnabled: false,
            profilingEnabled: false);

    private static WorkerReadModelIteration CreateIteration(
        WorkerReadModelWorker worker,
        long sequence,
        WorkCompletionStatus status)
        => new(
            worker.DefinitionId,
            new WorkerIterationReference(worker.Id, sequence),
            new WorkerIterationOverviewItem(
                worker.Id,
                sequence,
                worker.DefinitionName,
                worker.Category,
                worker.State,
                status,
                worker.UpdatedAt,
                worker.UpdatedAt,
                TimeSpan.Zero,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers.ToArray()));
}
