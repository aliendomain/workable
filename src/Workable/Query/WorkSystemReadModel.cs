using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;

namespace Workable;

internal interface IWorkSystemReadModelWriter
{
    void RecordWorker(WorkerReadModelWorker worker);

    void RecordIteration(WorkerReadModelIterationUpdate iteration);

    void ForgetIteration(WorkerIterationReference iteration);

    void ForgetWorker(WorkerId workerId);

    void ForgetWorkers(IReadOnlyCollection<WorkerId> workerIds);

    void Clear();
}

internal interface IWorkSystemReadModelReader
{
    WorkSystemReadModelSnapshot Current { get; }

    ValueTask Flush(CancellationToken cancellationToken = default);
}

internal interface IWorkSystemReadModelStore : IWorkSystemReadModelWriter, IWorkSystemReadModelReader;

internal sealed class WorkSystemReadModel : IWorkSystemReadModelStore, IAsyncDisposable
{
    private static readonly TimeSpan SnapshotPublishInterval = TimeSpan.FromMilliseconds(250);

    private readonly Channel<ReadModelInboxSignal> updates = Channel.CreateUnbounded<ReadModelInboxSignal>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly WorkSystemReadModelState state = new();
    private readonly Task projector;
    private readonly object projectionSync = new();
    private readonly object updateSync = new();
    private readonly Dictionary<ReadModelUpdateKey, PendingReadModelUpdate> pendingUpdates = [];
    private WorkSystemReadModelSnapshot snapshot = WorkSystemReadModelSnapshot.Empty;
    private TaskCompletionSource projectionAdvanced = CreateProjectionSignal();
    private Exception? projectorException;
    private long enqueuedSequence;
    private long appliedSequence;
    private long appliedUpdateCount;
    private long publishedSnapshotCount;
    private long lastBatchSize;
    private long lastProjectionDurationTicks;
    private long lastProjectedAtUnixTimeMilliseconds;
    private int publishRequested;
    private bool hasPendingSignal;

    public WorkSystemReadModel(
        WorkSystemCatalog catalog,
        Func<WorkSystemState> getSystemState,
        string? workSystemName,
        InMemoryWorkMetricsSink metrics)
    {
        this.Query = new WorkSystemReadModelQueryService(catalog, getSystemState, workSystemName, this, metrics);
        this.projector = Task.Run(this.Project);
    }

    public WorkSystemReadModelQueryService Query { get; }

    public WorkSystemReadModelSnapshot Current => Volatile.Read(ref this.snapshot);

    public void UseDetailReaders(
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>> getWorker,
        Func<WorkerIterationReference, CancellationToken, Task<WorkerIterationSnapshot?>> getIteration)
        => this.Query.UseDetailReaders(getWorker, getIteration);

    public WorkSystemReadModelDiagnostics Diagnostics
    {
        get
        {
            var exception = Volatile.Read(ref this.projectorException);
            var lastProjectedAt = Volatile.Read(ref this.lastProjectedAtUnixTimeMilliseconds);
            return new WorkSystemReadModelDiagnostics(
                Volatile.Read(ref this.enqueuedSequence),
                Volatile.Read(ref this.appliedSequence),
                Volatile.Read(ref this.appliedUpdateCount),
                Volatile.Read(ref this.publishedSnapshotCount),
                (int)Volatile.Read(ref this.lastBatchSize),
                TimeSpan.FromTicks(Volatile.Read(ref this.lastProjectionDurationTicks)),
                lastProjectedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastProjectedAt) : null,
                exception?.GetType().FullName,
                exception?.Message);
        }
    }

    public void RecordWorker(WorkerReadModelWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        this.Enqueue(sequence => new RecordWorkerUpdate(sequence, worker));
    }

    public void RecordIteration(WorkerReadModelIterationUpdate iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        this.Enqueue(sequence => new RecordIterationUpdate(sequence, iteration));
    }

    public void ForgetIteration(WorkerIterationReference iteration)
        => this.Enqueue(sequence => new ForgetIterationUpdate(sequence, iteration));

    public void ForgetWorker(WorkerId workerId)
        => this.Enqueue(sequence => new ForgetWorkerUpdate(sequence, workerId));

    public void ForgetWorkers(IReadOnlyCollection<WorkerId> workerIds)
    {
        ArgumentNullException.ThrowIfNull(workerIds);
        if (workerIds.Count == 0)
        {
            return;
        }

        var ids = workerIds.Distinct().ToArray();
        this.Enqueue(sequence => new ForgetWorkersUpdate(sequence, ids));
    }

    public void Clear()
        => this.Enqueue(sequence => new ClearReadModelUpdate(sequence));

    public void ThrowIfProjectorFailed()
        => ThrowIfProjectorFailed(Volatile.Read(ref this.projectorException));

    public async ValueTask Flush(CancellationToken cancellationToken = default)
    {
        var targetSequence = Volatile.Read(ref this.enqueuedSequence);
        while (Volatile.Read(ref this.appliedSequence) < targetSequence)
        {
            Volatile.Write(ref this.publishRequested, 1);
            ThrowIfProjectorFailed(Volatile.Read(ref this.projectorException));
            Task wait;
            lock (this.projectionSync)
            {
                if (Volatile.Read(ref this.appliedSequence) >= targetSequence)
                {
                    return;
                }

                wait = this.projectionAdvanced.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }

        ThrowIfProjectorFailed(Volatile.Read(ref this.projectorException));
    }

    public async ValueTask DisposeAsync()
    {
        this.updates.Writer.TryComplete();
        await this.projector.ConfigureAwait(false);
    }

    private void Enqueue(Func<long, ReadModelUpdate> createUpdate)
    {
        ArgumentNullException.ThrowIfNull(createUpdate);

        var signal = false;
        lock (this.updateSync)
        {
            var update = createUpdate(Interlocked.Increment(ref this.enqueuedSequence));
            this.StorePendingUpdateLocked(update);
            if (!this.hasPendingSignal)
            {
                this.hasPendingSignal = true;
                signal = true;
            }
        }

        if (signal)
        {
            this.updates.Writer.TryWrite(ReadModelInboxSignal.Value);
        }
    }

    private async Task Project()
    {
        try
        {
            var lastPublishedAt = DateTimeOffset.MinValue;
            var updatesSinceLastPublish = 0L;
            await foreach (var _ in this.updates.Reader.ReadAllAsync())
            {
                var batch = this.TakePendingUpdates();
                if (batch.Updates.Count == 0)
                {
                    continue;
                }

                foreach (var update in batch.Updates)
                {
                    this.Apply(update);
                }

                updatesSinceLastPublish += batch.UpdateCount;
                Interlocked.Add(ref this.appliedUpdateCount, batch.UpdateCount);
                var queueDrained = this.IsPendingQueueDrained();
                if (this.ShouldPublishSnapshot(queueDrained, lastPublishedAt))
                {
                    lastPublishedAt = this.Publish(batch.TargetSequence, updatesSinceLastPublish);
                    updatesSinceLastPublish = 0;
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref this.projectorException, exception);
            this.SignalProjectionAdvanced();
        }
    }

    private void StorePendingUpdateLocked(ReadModelUpdate update)
    {
        var representedUpdateCount = 1L;
        if (update is ClearReadModelUpdate)
        {
            representedUpdateCount += this.pendingUpdates.Values.Sum(pending => pending.UpdateCount);
            this.pendingUpdates.Clear();
        }
        else if (update is ForgetWorkerUpdate worker)
        {
            representedUpdateCount += this.RemovePendingIterationsLocked(worker.WorkerId);
        }
        else if (update is ForgetWorkersUpdate workers)
        {
            representedUpdateCount += this.RemovePendingWorkersLocked(workers.WorkerIds);
        }

        var key = ReadModelUpdateKey.From(update);
        if (this.pendingUpdates.Remove(key, out var existing))
        {
            representedUpdateCount += existing.UpdateCount;
        }

        this.pendingUpdates[key] = new PendingReadModelUpdate(update, representedUpdateCount);
    }

    private long RemovePendingIterationsLocked(WorkerId workerId)
    {
        var removed = 0L;
        foreach (var key in this.pendingUpdates.Keys
            .Where(key => key.Kind == ReadModelUpdateKind.Iteration && key.Iteration.WorkerId == workerId)
            .ToArray())
        {
            removed += this.pendingUpdates[key].UpdateCount;
            this.pendingUpdates.Remove(key);
        }

        return removed;
    }

    private long RemovePendingWorkersLocked(IReadOnlyCollection<WorkerId> workerIds)
    {
        var removed = 0L;
        var ids = workerIds.Count > 4 ? workerIds.ToHashSet() : null;
        foreach (var key in this.pendingUpdates.Keys
            .Where(key =>
                ContainsWorker(workerIds, ids, key.WorkerId) ||
                (key.Kind == ReadModelUpdateKind.Iteration && ContainsWorker(workerIds, ids, key.Iteration.WorkerId)))
            .ToArray())
        {
            removed += this.pendingUpdates[key].UpdateCount;
            this.pendingUpdates.Remove(key);
        }

        return removed;
    }

    private PendingReadModelBatch TakePendingUpdates()
    {
        lock (this.updateSync)
        {
            if (this.pendingUpdates.Count == 0)
            {
                this.hasPendingSignal = false;
                return PendingReadModelBatch.Empty;
            }

            var updates = this.pendingUpdates.Values
                .OrderBy(pending => pending.Update.Sequence)
                .Select(pending => pending.Update)
                .ToArray();
            var targetSequence = updates[^1].Sequence;
            var updateCount = this.pendingUpdates.Values.Sum(pending => pending.UpdateCount);
            this.pendingUpdates.Clear();
            this.hasPendingSignal = false;
            return new PendingReadModelBatch(updates, targetSequence, updateCount);
        }
    }

    private bool IsPendingQueueDrained()
    {
        lock (this.updateSync)
        {
            return this.pendingUpdates.Count == 0 && !this.hasPendingSignal;
        }
    }

    private void Apply(ReadModelUpdate update)
    {
        switch (update)
        {
            case RecordWorkerUpdate worker:
                this.state.RecordWorker(worker.Worker, update.Sequence);
                break;
            case RecordIterationUpdate iteration:
                this.state.RecordIteration(iteration.Iteration, update.Sequence);
                break;
            case ForgetIterationUpdate iteration:
                this.state.ForgetIteration(iteration.Iteration, update.Sequence);
                break;
            case ForgetWorkerUpdate worker:
                this.state.ForgetWorker(worker.WorkerId, update.Sequence);
                break;
            case ForgetWorkersUpdate workers:
                this.state.ForgetWorkers(workers.WorkerIds, update.Sequence);
                break;
            case ClearReadModelUpdate clear:
                this.state.Clear(clear.Sequence);
                break;
        }
    }

    private bool ShouldPublishSnapshot(bool queueDrained, DateTimeOffset lastPublishedAt)
        => queueDrained ||
            Volatile.Read(ref this.publishRequested) == 1 ||
            lastPublishedAt == DateTimeOffset.MinValue ||
            DateTimeOffset.UtcNow - lastPublishedAt >= SnapshotPublishInterval;

    private DateTimeOffset Publish(long sequence, long batchSize)
    {
        Interlocked.Exchange(ref this.publishRequested, 0);
        var stopwatch = Stopwatch.StartNew();
        Volatile.Write(ref this.snapshot, this.state.ToSnapshot());
        stopwatch.Stop();
        var publishedAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref this.appliedSequence, sequence);
        Interlocked.Increment(ref this.publishedSnapshotCount);
        Volatile.Write(ref this.lastBatchSize, batchSize);
        Volatile.Write(ref this.lastProjectionDurationTicks, stopwatch.Elapsed.Ticks);
        Volatile.Write(ref this.lastProjectedAtUnixTimeMilliseconds, publishedAt.ToUnixTimeMilliseconds());
        this.SignalProjectionAdvanced();
        return publishedAt;
    }

    private void SignalProjectionAdvanced()
    {
        TaskCompletionSource completed;
        lock (this.projectionSync)
        {
            completed = this.projectionAdvanced;
            this.projectionAdvanced = CreateProjectionSignal();
        }

        completed.TrySetResult();
    }

    private static TaskCompletionSource CreateProjectionSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ThrowIfProjectorFailed(Exception? exception)
    {
        if (exception is not null)
        {
            throw new InvalidOperationException("The Workable read model projector failed.", exception);
        }
    }

    private static bool ContainsWorker(
        IReadOnlyCollection<WorkerId> workerIds,
        HashSet<WorkerId>? workerIdSet,
        WorkerId workerId)
        => workerIdSet?.Contains(workerId) ?? workerIds.Contains(workerId);

    private readonly record struct ReadModelInboxSignal
    {
        public static ReadModelInboxSignal Value { get; } = new();
    }

    private enum ReadModelUpdateKind
    {
        Worker,
        Iteration,
        WorkerBatch,
        Clear,
    }

    private readonly record struct ReadModelUpdateKey(
        ReadModelUpdateKind Kind,
        WorkerId WorkerId,
        WorkerIterationReference Iteration,
        long BatchSequence)
    {
        public static ReadModelUpdateKey From(ReadModelUpdate update)
            => update switch
            {
                RecordWorkerUpdate worker => Worker(worker.Worker.Id),
                RecordIterationUpdate iteration => IterationKey(iteration.Iteration.Iteration.Reference),
                ForgetIterationUpdate iteration => IterationKey(iteration.Iteration),
                ForgetWorkerUpdate worker => Worker(worker.WorkerId),
                ForgetWorkersUpdate workers => WorkerBatch(workers.Sequence),
                ClearReadModelUpdate => Clear(),
                _ => throw new InvalidOperationException($"Unknown read model update '{update.GetType().FullName}'."),
            };

        private static ReadModelUpdateKey Worker(WorkerId workerId)
            => new(ReadModelUpdateKind.Worker, workerId, default, 0);

        private static ReadModelUpdateKey IterationKey(WorkerIterationReference iteration)
            => new(ReadModelUpdateKind.Iteration, iteration.WorkerId, iteration, 0);

        private static ReadModelUpdateKey WorkerBatch(long sequence)
            => new(ReadModelUpdateKind.WorkerBatch, default, default, sequence);

        private static ReadModelUpdateKey Clear()
            => new(ReadModelUpdateKind.Clear, default, default, 0);
    }

    private sealed record PendingReadModelUpdate(
        ReadModelUpdate Update,
        long UpdateCount);

    private sealed record PendingReadModelBatch(
        IReadOnlyList<ReadModelUpdate> Updates,
        long TargetSequence,
        long UpdateCount)
    {
        public static PendingReadModelBatch Empty { get; } = new([], 0, 0);
    }

    private abstract record ReadModelUpdate(long Sequence);

    private sealed record RecordWorkerUpdate(long Sequence, WorkerReadModelWorker Worker) : ReadModelUpdate(Sequence);

    private sealed record RecordIterationUpdate(long Sequence, WorkerReadModelIterationUpdate Iteration) : ReadModelUpdate(Sequence);

    private sealed record ForgetIterationUpdate(long Sequence, WorkerIterationReference Iteration) : ReadModelUpdate(Sequence);

    private sealed record ForgetWorkerUpdate(long Sequence, WorkerId WorkerId) : ReadModelUpdate(Sequence);

    private sealed record ForgetWorkersUpdate(long Sequence, IReadOnlyList<WorkerId> WorkerIds) : ReadModelUpdate(Sequence);

    private sealed record ClearReadModelUpdate(long Sequence) : ReadModelUpdate(Sequence);
}

internal sealed class WorkSystemReadModelState
{
    private readonly Dictionary<WorkerId, WorkerReadModelWorker> workers = [];
    private readonly Dictionary<WorkerId, long> workerSequences = [];
    private readonly Dictionary<WorkerIterationReference, WorkerReadModelIteration> iterations = [];
    private readonly Dictionary<WorkerIterationReference, long> iterationSequences = [];
    private readonly Dictionary<WorkDefinitionId, HashSet<WorkerReadModelWorker>> workersByDefinition = [];
    private readonly Dictionary<WorkerState, HashSet<WorkerReadModelWorker>> workersByState = [];
    private readonly Dictionary<(WorkDefinitionId DefinitionId, WorkerState State), HashSet<WorkerReadModelWorker>> workersByDefinitionAndState = [];
    private readonly Dictionary<WorkSubjectId, HashSet<WorkerReadModelWorker>> workersBySubject = [];
    private readonly Dictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), HashSet<WorkerReadModelWorker>> workersByDefinitionAndSubject = [];
    private readonly Dictionary<WorkConcurrencyKey, HashSet<WorkerReadModelWorker>> workersByConcurrencyKey = [];
    private readonly Dictionary<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), HashSet<WorkerReadModelWorker>> workersByDefinitionAndConcurrencyKey = [];
    private readonly Dictionary<WorkIdentifier, HashSet<WorkerReadModelWorker>> workersByIdentifier = [];
    private readonly Dictionary<bool, HashSet<WorkerReadModelWorker>> workersByRecurrenceEnabled = [];
    private readonly Dictionary<bool, HashSet<WorkerReadModelWorker>> workersByConcurrencyEnabled = [];
    private readonly Dictionary<bool, HashSet<WorkerReadModelWorker>> workersByProfilingEnabled = [];
    private readonly Dictionary<WorkerId, HashSet<WorkerReadModelIteration>> iterationsByWorker = [];
    private readonly Dictionary<WorkDefinitionId, HashSet<WorkerReadModelIteration>> iterationsByDefinition = [];
    private readonly Dictionary<WorkCompletionStatus, HashSet<WorkerReadModelIteration>> iterationsByStatus = [];
    private readonly Dictionary<(WorkDefinitionId DefinitionId, WorkCompletionStatus Status), HashSet<WorkerReadModelIteration>> iterationsByDefinitionAndStatus = [];
    private readonly Dictionary<WorkSubjectId, HashSet<WorkerReadModelIteration>> iterationsBySubject = [];
    private readonly Dictionary<WorkConcurrencyKey, HashSet<WorkerReadModelIteration>> iterationsByConcurrencyKey = [];
    private readonly Dictionary<WorkIdentifier, HashSet<WorkerReadModelIteration>> iterationsByIdentifier = [];
    private readonly HashSet<WorkerReadModelKey> workerKeys = [];
    private readonly HashSet<WorkerIterationReadModelKey> iterationKeys = [];
    private long clearedSequence;

    public void RecordWorker(WorkerReadModelWorker worker, long sequence)
    {
        if (sequence < this.clearedSequence ||
            (this.workerSequences.TryGetValue(worker.Id, out var existingSequence) && existingSequence > sequence))
        {
            return;
        }

        if (this.workers.TryGetValue(worker.Id, out var existing))
        {
            this.RemoveWorkerIndexes(existing);
        }

        this.workers[worker.Id] = worker;
        this.workerSequences[worker.Id] = sequence;
        this.AddWorkerIndexes(worker);
    }

    public void RecordIteration(WorkerReadModelIterationUpdate iteration, long sequence)
    {
        if (sequence < this.clearedSequence)
        {
            return;
        }

        this.RecordWorker(iteration.Worker, sequence);
        if (this.iterationSequences.TryGetValue(iteration.Iteration.Reference, out var existingSequence) &&
            existingSequence > sequence)
        {
            return;
        }

        if (this.iterations.TryGetValue(iteration.Iteration.Reference, out var existing))
        {
            this.RemoveIterationIndexes(existing);
        }

        this.iterations[iteration.Iteration.Reference] = iteration.Iteration;
        this.iterationSequences[iteration.Iteration.Reference] = sequence;
        this.AddIterationIndexes(iteration.Iteration);
    }

    public void ForgetIteration(WorkerIterationReference iteration, long sequence)
    {
        if (sequence < this.clearedSequence ||
            (this.iterationSequences.TryGetValue(iteration, out var existingSequence) && existingSequence > sequence))
        {
            return;
        }

        if (this.iterations.Remove(iteration, out var existing))
        {
            this.RemoveIterationIndexes(existing);
        }

        this.iterationSequences.Remove(iteration);
    }

    public void ForgetWorker(WorkerId workerId, long sequence)
    {
        if (sequence < this.clearedSequence ||
            (this.workerSequences.TryGetValue(workerId, out var existingSequence) && existingSequence > sequence))
        {
            return;
        }

        if (this.workers.Remove(workerId, out var existing))
        {
            this.RemoveWorkerIndexes(existing);
        }

        this.workerSequences.Remove(workerId);
        var workerIterations = this.iterationsByWorker.TryGetValue(workerId, out var indexed)
            ? indexed.ToArray()
            : [];
        foreach (var reference in workerIterations.Select(iteration => iteration.Reference))
        {
            if (this.iterationSequences.TryGetValue(reference, out var iterationSequence) && iterationSequence > sequence)
            {
                continue;
            }

            if (this.iterations.Remove(reference, out var existingIteration))
            {
                this.RemoveIterationIndexes(existingIteration);
            }

            this.iterationSequences.Remove(reference);
        }
    }

    public void ForgetWorkers(IReadOnlyCollection<WorkerId> workerIds, long sequence)
    {
        foreach (var workerId in workerIds)
        {
            this.ForgetWorker(workerId, sequence);
        }
    }

    public void Clear(long sequence)
    {
        if (sequence < this.clearedSequence)
        {
            return;
        }

        this.clearedSequence = sequence;
        this.workers.Clear();
        this.workerSequences.Clear();
        this.iterations.Clear();
        this.iterationSequences.Clear();
        this.workersByDefinition.Clear();
        this.workersByState.Clear();
        this.workersByDefinitionAndState.Clear();
        this.workersBySubject.Clear();
        this.workersByDefinitionAndSubject.Clear();
        this.workersByConcurrencyKey.Clear();
        this.workersByDefinitionAndConcurrencyKey.Clear();
        this.workersByIdentifier.Clear();
        this.workersByRecurrenceEnabled.Clear();
        this.workersByConcurrencyEnabled.Clear();
        this.workersByProfilingEnabled.Clear();
        this.iterationsByWorker.Clear();
        this.iterationsByDefinition.Clear();
        this.iterationsByStatus.Clear();
        this.iterationsByDefinitionAndStatus.Clear();
        this.iterationsBySubject.Clear();
        this.iterationsByConcurrencyKey.Clear();
        this.iterationsByIdentifier.Clear();
        this.workerKeys.Clear();
        this.iterationKeys.Clear();
    }

    public WorkSystemReadModelSnapshot ToSnapshot()
        => new(
            this.workers.ToDictionary(entry => entry.Key, entry => entry.Value),
            this.iterations.ToDictionary(entry => entry.Key, entry => entry.Value),
            this.workers.Values.ToArray(),
            this.iterations.Values.ToArray(),
            FreezeIndex(this.workersByDefinition),
            FreezeIndex(this.workersByState),
            FreezeIndex(this.workersByDefinitionAndState),
            FreezeIndex(this.workersBySubject),
            FreezeIndex(this.workersByDefinitionAndSubject),
            FreezeIndex(this.workersByConcurrencyKey),
            FreezeIndex(this.workersByDefinitionAndConcurrencyKey),
            FreezeIndex(this.workersByIdentifier),
            FreezeIndex(this.workersByRecurrenceEnabled),
            FreezeIndex(this.workersByConcurrencyEnabled),
            FreezeIndex(this.workersByProfilingEnabled),
            FreezeIndex(this.iterationsByWorker),
            FreezeIndex(this.iterationsByDefinition),
            FreezeIndex(this.iterationsByStatus),
            FreezeIndex(this.iterationsByDefinitionAndStatus),
            FreezeIndex(this.iterationsBySubject),
            FreezeIndex(this.iterationsByConcurrencyKey),
            FreezeIndex(this.iterationsByIdentifier),
            this.workerKeys.ToArray(),
            this.iterationKeys.ToArray());

    private void AddWorkerIndexes(WorkerReadModelWorker worker)
    {
        AddIndex(this.workersByDefinition, worker.DefinitionId, worker);
        AddIndex(this.workersByState, worker.State, worker);
        AddIndex(this.workersByDefinitionAndState, (worker.DefinitionId, worker.State), worker);
        AddIndex(this.workersByRecurrenceEnabled, worker.RecurrenceEnabled, worker);
        AddIndex(this.workersByConcurrencyEnabled, worker.ConcurrencyEnabled, worker);
        AddIndex(this.workersByProfilingEnabled, worker.ProfilingEnabled, worker);

        if (worker.SubjectId is { } subjectId)
        {
            AddIndex(this.workersBySubject, subjectId, worker);
            AddIndex(this.workersByDefinitionAndSubject, (worker.DefinitionId, subjectId), worker);
        }

        if (worker.ConcurrencyKey is { } concurrencyKey)
        {
            AddIndex(this.workersByConcurrencyKey, concurrencyKey, worker);
            AddIndex(this.workersByDefinitionAndConcurrencyKey, (worker.DefinitionId, concurrencyKey), worker);
        }

        foreach (var identifier in worker.Identifiers)
        {
            AddIndex(this.workersByIdentifier, identifier, worker);
        }

        foreach (var key in WorkerReadModelKey.From(worker))
        {
            this.workerKeys.Add(key);
        }
    }

    private void RemoveWorkerIndexes(WorkerReadModelWorker worker)
    {
        RemoveIndex(this.workersByDefinition, worker.DefinitionId, worker);
        RemoveIndex(this.workersByState, worker.State, worker);
        RemoveIndex(this.workersByDefinitionAndState, (worker.DefinitionId, worker.State), worker);
        RemoveIndex(this.workersByRecurrenceEnabled, worker.RecurrenceEnabled, worker);
        RemoveIndex(this.workersByConcurrencyEnabled, worker.ConcurrencyEnabled, worker);
        RemoveIndex(this.workersByProfilingEnabled, worker.ProfilingEnabled, worker);

        if (worker.SubjectId is { } subjectId)
        {
            RemoveIndex(this.workersBySubject, subjectId, worker);
            RemoveIndex(this.workersByDefinitionAndSubject, (worker.DefinitionId, subjectId), worker);
        }

        if (worker.ConcurrencyKey is { } concurrencyKey)
        {
            RemoveIndex(this.workersByConcurrencyKey, concurrencyKey, worker);
            RemoveIndex(this.workersByDefinitionAndConcurrencyKey, (worker.DefinitionId, concurrencyKey), worker);
        }

        foreach (var identifier in worker.Identifiers)
        {
            RemoveIndex(this.workersByIdentifier, identifier, worker);
        }

        foreach (var key in WorkerReadModelKey.From(worker))
        {
            this.workerKeys.Remove(key);
        }
    }

    private void AddIterationIndexes(WorkerReadModelIteration iteration)
    {
        AddIndex(this.iterationsByWorker, iteration.WorkerId, iteration);
        AddIndex(this.iterationsByDefinition, iteration.DefinitionId, iteration);
        AddIndex(this.iterationsByStatus, iteration.Status, iteration);
        AddIndex(this.iterationsByDefinitionAndStatus, (iteration.DefinitionId, iteration.Status), iteration);

        if (iteration.SubjectId is { } subjectId)
        {
            AddIndex(this.iterationsBySubject, subjectId, iteration);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            AddIndex(this.iterationsByConcurrencyKey, concurrencyKey, iteration);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            AddIndex(this.iterationsByIdentifier, identifier, iteration);
        }

        foreach (var key in WorkerIterationReadModelKey.From(iteration))
        {
            this.iterationKeys.Add(key);
        }
    }

    private void RemoveIterationIndexes(WorkerReadModelIteration iteration)
    {
        RemoveIndex(this.iterationsByWorker, iteration.WorkerId, iteration);
        RemoveIndex(this.iterationsByDefinition, iteration.DefinitionId, iteration);
        RemoveIndex(this.iterationsByStatus, iteration.Status, iteration);
        RemoveIndex(this.iterationsByDefinitionAndStatus, (iteration.DefinitionId, iteration.Status), iteration);

        if (iteration.SubjectId is { } subjectId)
        {
            RemoveIndex(this.iterationsBySubject, subjectId, iteration);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            RemoveIndex(this.iterationsByConcurrencyKey, concurrencyKey, iteration);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            RemoveIndex(this.iterationsByIdentifier, identifier, iteration);
        }

        foreach (var key in WorkerIterationReadModelKey.From(iteration))
        {
            this.iterationKeys.Remove(key);
        }
    }

    private static void AddIndex<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> index,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var values))
        {
            values = [];
            index[key] = values;
        }

        values.Add(value);
    }

    private static void RemoveIndex<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> index,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var values))
        {
            return;
        }

        values.Remove(value);
        if (values.Count == 0)
        {
            index.Remove(key);
        }
    }

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> FreezeIndex<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> index)
        where TKey : notnull
        => index.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<TValue>)entry.Value.ToArray());
}

internal sealed class WorkSystemReadModelSnapshot
{
    public static WorkSystemReadModelSnapshot Empty { get; } = new(
        new Dictionary<WorkerId, WorkerReadModelWorker>(),
        new Dictionary<WorkerIterationReference, WorkerReadModelIteration>(),
        Array.Empty<WorkerReadModelWorker>(),
        Array.Empty<WorkerReadModelIteration>(),
        EmptyIndex<WorkDefinitionId, WorkerReadModelWorker>(),
        EmptyIndex<WorkerState, WorkerReadModelWorker>(),
        EmptyIndex<(WorkDefinitionId DefinitionId, WorkerState State), WorkerReadModelWorker>(),
        EmptyIndex<WorkSubjectId, WorkerReadModelWorker>(),
        EmptyIndex<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), WorkerReadModelWorker>(),
        EmptyIndex<WorkConcurrencyKey, WorkerReadModelWorker>(),
        EmptyIndex<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), WorkerReadModelWorker>(),
        EmptyIndex<WorkIdentifier, WorkerReadModelWorker>(),
        EmptyIndex<bool, WorkerReadModelWorker>(),
        EmptyIndex<bool, WorkerReadModelWorker>(),
        EmptyIndex<bool, WorkerReadModelWorker>(),
        EmptyIndex<WorkerId, WorkerReadModelIteration>(),
        EmptyIndex<WorkDefinitionId, WorkerReadModelIteration>(),
        EmptyIndex<WorkCompletionStatus, WorkerReadModelIteration>(),
        EmptyIndex<(WorkDefinitionId DefinitionId, WorkCompletionStatus Status), WorkerReadModelIteration>(),
        EmptyIndex<WorkSubjectId, WorkerReadModelIteration>(),
        EmptyIndex<WorkConcurrencyKey, WorkerReadModelIteration>(),
        EmptyIndex<WorkIdentifier, WorkerReadModelIteration>(),
        Array.Empty<WorkerReadModelKey>(),
        Array.Empty<WorkerIterationReadModelKey>());

    public WorkSystemReadModelSnapshot(
        IReadOnlyDictionary<WorkerId, WorkerReadModelWorker> workersById,
        IReadOnlyDictionary<WorkerIterationReference, WorkerReadModelIteration> iterationsByReference,
        IReadOnlyList<WorkerReadModelWorker> workers,
        IReadOnlyList<WorkerReadModelIteration> iterations,
        IReadOnlyDictionary<WorkDefinitionId, IReadOnlyList<WorkerReadModelWorker>> workersByDefinition,
        IReadOnlyDictionary<WorkerState, IReadOnlyList<WorkerReadModelWorker>> workersByState,
        IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkerState State), IReadOnlyList<WorkerReadModelWorker>> workersByDefinitionAndState,
        IReadOnlyDictionary<WorkSubjectId, IReadOnlyList<WorkerReadModelWorker>> workersBySubject,
        IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), IReadOnlyList<WorkerReadModelWorker>> workersByDefinitionAndSubject,
        IReadOnlyDictionary<WorkConcurrencyKey, IReadOnlyList<WorkerReadModelWorker>> workersByConcurrencyKey,
        IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), IReadOnlyList<WorkerReadModelWorker>> workersByDefinitionAndConcurrencyKey,
        IReadOnlyDictionary<WorkIdentifier, IReadOnlyList<WorkerReadModelWorker>> workersByIdentifier,
        IReadOnlyDictionary<bool, IReadOnlyList<WorkerReadModelWorker>> workersByRecurrenceEnabled,
        IReadOnlyDictionary<bool, IReadOnlyList<WorkerReadModelWorker>> workersByConcurrencyEnabled,
        IReadOnlyDictionary<bool, IReadOnlyList<WorkerReadModelWorker>> workersByProfilingEnabled,
        IReadOnlyDictionary<WorkerId, IReadOnlyList<WorkerReadModelIteration>> iterationsByWorker,
        IReadOnlyDictionary<WorkDefinitionId, IReadOnlyList<WorkerReadModelIteration>> iterationsByDefinition,
        IReadOnlyDictionary<WorkCompletionStatus, IReadOnlyList<WorkerReadModelIteration>> iterationsByStatus,
        IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkCompletionStatus Status), IReadOnlyList<WorkerReadModelIteration>> iterationsByDefinitionAndStatus,
        IReadOnlyDictionary<WorkSubjectId, IReadOnlyList<WorkerReadModelIteration>> iterationsBySubject,
        IReadOnlyDictionary<WorkConcurrencyKey, IReadOnlyList<WorkerReadModelIteration>> iterationsByConcurrencyKey,
        IReadOnlyDictionary<WorkIdentifier, IReadOnlyList<WorkerReadModelIteration>> iterationsByIdentifier,
        IReadOnlyList<WorkerReadModelKey> workerKeys,
        IReadOnlyList<WorkerIterationReadModelKey> iterationKeys)
    {
        this.WorkersById = workersById;
        this.IterationsByReference = iterationsByReference;
        this.Workers = workers;
        this.Iterations = iterations;
        this.WorkersByDefinition = workersByDefinition;
        this.WorkersByState = workersByState;
        this.WorkersByDefinitionAndState = workersByDefinitionAndState;
        this.WorkersBySubject = workersBySubject;
        this.WorkersByDefinitionAndSubject = workersByDefinitionAndSubject;
        this.WorkersByConcurrencyKey = workersByConcurrencyKey;
        this.WorkersByDefinitionAndConcurrencyKey = workersByDefinitionAndConcurrencyKey;
        this.WorkersByIdentifier = workersByIdentifier;
        this.WorkersByRecurrenceEnabled = workersByRecurrenceEnabled;
        this.WorkersByConcurrencyEnabled = workersByConcurrencyEnabled;
        this.WorkersByProfilingEnabled = workersByProfilingEnabled;
        this.IterationsByWorker = iterationsByWorker;
        this.IterationsByDefinition = iterationsByDefinition;
        this.IterationsByStatus = iterationsByStatus;
        this.IterationsByDefinitionAndStatus = iterationsByDefinitionAndStatus;
        this.IterationsBySubject = iterationsBySubject;
        this.IterationsByConcurrencyKey = iterationsByConcurrencyKey;
        this.IterationsByIdentifier = iterationsByIdentifier;
        this.WorkerKeys = workerKeys;
        this.IterationKeys = iterationKeys;
    }

    public IReadOnlyDictionary<WorkerId, WorkerReadModelWorker> WorkersById { get; }

    public IReadOnlyDictionary<WorkerIterationReference, WorkerReadModelIteration> IterationsByReference { get; }

    public IReadOnlyList<WorkerReadModelWorker> Workers { get; }

    public IReadOnlyList<WorkerReadModelIteration> Iterations { get; }

    public IReadOnlyDictionary<WorkDefinitionId, IReadOnlyList<WorkerReadModelWorker>> WorkersByDefinition { get; }

    public IReadOnlyDictionary<WorkerState, IReadOnlyList<WorkerReadModelWorker>> WorkersByState { get; }

    public IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkerState State), IReadOnlyList<WorkerReadModelWorker>> WorkersByDefinitionAndState { get; }

    public IReadOnlyDictionary<WorkSubjectId, IReadOnlyList<WorkerReadModelWorker>> WorkersBySubject { get; }

    public IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), IReadOnlyList<WorkerReadModelWorker>> WorkersByDefinitionAndSubject { get; }

    public IReadOnlyDictionary<WorkConcurrencyKey, IReadOnlyList<WorkerReadModelWorker>> WorkersByConcurrencyKey { get; }

    public IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), IReadOnlyList<WorkerReadModelWorker>> WorkersByDefinitionAndConcurrencyKey { get; }

    public IReadOnlyDictionary<WorkIdentifier, IReadOnlyList<WorkerReadModelWorker>> WorkersByIdentifier { get; }

    public IReadOnlyDictionary<bool, IReadOnlyList<WorkerReadModelWorker>> WorkersByRecurrenceEnabled { get; }

    public IReadOnlyDictionary<bool, IReadOnlyList<WorkerReadModelWorker>> WorkersByConcurrencyEnabled { get; }

    public IReadOnlyDictionary<bool, IReadOnlyList<WorkerReadModelWorker>> WorkersByProfilingEnabled { get; }

    public IReadOnlyDictionary<WorkerId, IReadOnlyList<WorkerReadModelIteration>> IterationsByWorker { get; }

    public IReadOnlyDictionary<WorkDefinitionId, IReadOnlyList<WorkerReadModelIteration>> IterationsByDefinition { get; }

    public IReadOnlyDictionary<WorkCompletionStatus, IReadOnlyList<WorkerReadModelIteration>> IterationsByStatus { get; }

    public IReadOnlyDictionary<(WorkDefinitionId DefinitionId, WorkCompletionStatus Status), IReadOnlyList<WorkerReadModelIteration>> IterationsByDefinitionAndStatus { get; }

    public IReadOnlyDictionary<WorkSubjectId, IReadOnlyList<WorkerReadModelIteration>> IterationsBySubject { get; }

    public IReadOnlyDictionary<WorkConcurrencyKey, IReadOnlyList<WorkerReadModelIteration>> IterationsByConcurrencyKey { get; }

    public IReadOnlyDictionary<WorkIdentifier, IReadOnlyList<WorkerReadModelIteration>> IterationsByIdentifier { get; }

    public IReadOnlyList<WorkerReadModelKey> WorkerKeys { get; }

    public IReadOnlyList<WorkerIterationReadModelKey> IterationKeys { get; }

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> EmptyIndex<TKey, TValue>()
        where TKey : notnull
        => new Dictionary<TKey, IReadOnlyList<TValue>>();
}

internal sealed record WorkerReadModelWorker(
    WorkerOverviewItem Overview,
    bool RecurrenceEnabled,
    bool ConcurrencyEnabled,
    bool ProfilingEnabled)
{
    public WorkerId Id => this.Overview.Id;

    public WorkDefinitionId DefinitionId => this.Overview.DefinitionId;

    public string DefinitionName => this.Overview.DefinitionName;

    public WorkSubjectId? SubjectId => this.Overview.SubjectId;

    public WorkConcurrencyKey? ConcurrencyKey => this.Overview.ConcurrencyKey;

    public IReadOnlySet<WorkIdentifier> Identifiers => this.Overview.Identifiers;

    public long Revision => this.Overview.Revision;

    public string Category => this.Overview.Category;

    public WorkerState State => this.Overview.State;

    public DateTimeOffset CreatedAt => this.Overview.CreatedAt;

    public DateTimeOffset StateChangedAt => this.Overview.StateChangedAt;

    public DateTimeOffset UpdatedAt => this.Overview.UpdatedAt;

    public static WorkerReadModelWorker From(WorkerSnapshot snapshot)
        => From(
            new WorkerOverviewItem(
                snapshot.Id,
                snapshot.DefinitionId,
                snapshot.DefinitionName,
                snapshot.SubjectId,
                snapshot.ConcurrencyKey,
                snapshot.Identifiers,
                snapshot.Revision,
                snapshot.DefinitionCategory,
                snapshot.State,
                snapshot.InterruptionReason,
                snapshot.CreatedAt,
                snapshot.StateChangedAt,
                snapshot.UpdatedAt)
            {
                QueueDuration = snapshot.QueueDuration,
                TotalExecutionDuration = snapshot.TotalExecutionDuration,
                NextRunAt = snapshot.NextRunAt,
            },
            snapshot.Configuration.Recurrence.IsEnabled,
            snapshot.Configuration.Concurrency.IsEnabled,
            snapshot.Options.ProfilingEnabled);

    public static WorkerReadModelWorker From(
        WorkerOverviewItem overview,
        bool recurrenceEnabled,
        bool concurrencyEnabled,
        bool profilingEnabled)
        => new(overview, recurrenceEnabled, concurrencyEnabled, profilingEnabled);
}

internal sealed record WorkerReadModelIteration(
    WorkerIterationReference Reference,
    WorkerIterationOverviewItem Overview)
{
    public WorkerId WorkerId => this.Reference.WorkerId;

    public long Sequence => this.Reference.Sequence;

    public WorkDefinitionId DefinitionId => this.Overview.DefinitionId;

    public string DefinitionName => this.Overview.DefinitionName;

    public string Category => this.Overview.Category;

    public WorkerState WorkerState => this.Overview.WorkerState;

    public WorkCompletionStatus Status => this.Overview.Status;

    public DateTimeOffset StartedAt => this.Overview.StartedAt;

    public DateTimeOffset CompletedAt => this.Overview.CompletedAt;

    public TimeSpan ExecutionDuration => this.Overview.ExecutionDuration;

    public WorkSubjectId? SubjectId => this.Overview.SubjectId;

    public WorkConcurrencyKey? ConcurrencyKey => this.Overview.ConcurrencyKey;

    public IReadOnlyCollection<WorkIdentifier> Identifiers => this.Overview.Identifiers;

    public static WorkerReadModelIteration From(WorkerReadModelWorker worker, WorkerIterationSnapshot iteration)
    {
        var reference = new WorkerIterationReference(worker.Id, iteration.Sequence);
        return new(
            reference,
            new WorkerIterationOverviewItem(
                worker.Id,
                iteration.Sequence,
                worker.DefinitionId,
                worker.DefinitionName,
                worker.Category,
                worker.State,
                iteration.Status,
                iteration.StartedAt,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                worker.SubjectId,
                worker.ConcurrencyKey,
                [.. worker.Identifiers]));
    }
}

internal sealed record WorkerReadModelIterationUpdate(
    WorkerReadModelWorker Worker,
    WorkerReadModelIteration Iteration,
    WorkerIterationSnapshot Snapshot);

internal sealed record WorkerReadModelKey(
    WorkKeyKind Kind,
    string Type,
    string Value,
    WorkerReadModelWorker Worker)
{
    public static IEnumerable<WorkerReadModelKey> From(WorkerReadModelWorker worker)
    {
        if (worker.SubjectId is { } subjectId)
        {
            yield return new WorkerReadModelKey(WorkKeyKind.Subject, subjectId.Type, subjectId.Value, worker);
        }

        if (worker.ConcurrencyKey is { } concurrencyKey)
        {
            yield return new WorkerReadModelKey(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value, worker);
        }

        foreach (var identifier in worker.Identifiers)
        {
            yield return new WorkerReadModelKey(WorkKeyKind.Identifier, identifier.Type, identifier.Value, worker);
        }
    }
}

internal sealed record WorkerIterationReadModelKey(
    WorkKeyKind Kind,
    string Type,
    string Value,
    WorkerReadModelIteration Iteration)
{
    public static IEnumerable<WorkerIterationReadModelKey> From(WorkerReadModelIteration iteration)
    {
        if (iteration.SubjectId is { } subjectId)
        {
            yield return new WorkerIterationReadModelKey(WorkKeyKind.Subject, subjectId.Type, subjectId.Value, iteration);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            yield return new WorkerIterationReadModelKey(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value, iteration);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            yield return new WorkerIterationReadModelKey(WorkKeyKind.Identifier, identifier.Type, identifier.Value, iteration);
        }
    }
}
