using System.Threading.Channels;

namespace Workable;

internal interface IWorkSystemReadModelWriter
{
    void RecordWorker(WorkerReadModelWorker worker);

    void RecordIteration(WorkerReadModelIterationUpdate iteration);

    void ForgetIteration(WorkerIterationReference iteration);

    void ForgetWorker(WorkerId workerId);

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
    private const int ProjectorBatchSize = 1024;

    private readonly Channel<ReadModelUpdate> updates = Channel.CreateUnbounded<ReadModelUpdate>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly WorkSystemReadModelState state = new();
    private readonly Task projector;
    private readonly object projectionSync = new();
    private WorkSystemReadModelSnapshot snapshot = WorkSystemReadModelSnapshot.Empty;
    private TaskCompletionSource projectionAdvanced = CreateProjectionSignal();
    private Exception? projectorException;
    private long enqueuedSequence;
    private long appliedSequence;

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

    public void RecordWorker(WorkerReadModelWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        this.Enqueue(new RecordWorkerUpdate(this.NextSequence(), worker));
    }

    public void RecordIteration(WorkerReadModelIterationUpdate iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        this.Enqueue(new RecordIterationUpdate(this.NextSequence(), iteration));
    }

    public void ForgetIteration(WorkerIterationReference iteration)
        => this.Enqueue(new ForgetIterationUpdate(this.NextSequence(), iteration));

    public void ForgetWorker(WorkerId workerId)
        => this.Enqueue(new ForgetWorkerUpdate(this.NextSequence(), workerId));

    public void Clear()
        => this.Enqueue(new ClearReadModelUpdate(this.NextSequence()));

    public async ValueTask Flush(CancellationToken cancellationToken = default)
    {
        var targetSequence = Volatile.Read(ref this.enqueuedSequence);
        while (Volatile.Read(ref this.appliedSequence) < targetSequence)
        {
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

    private long NextSequence()
        => Interlocked.Increment(ref this.enqueuedSequence);

    private void Enqueue(ReadModelUpdate update)
        => this.updates.Writer.TryWrite(update);

    private async Task Project()
    {
        try
        {
            await foreach (var first in this.updates.Reader.ReadAllAsync())
            {
                var lastSequence = this.Apply(first);
                var remaining = ProjectorBatchSize - 1;
                while (remaining > 0 && this.updates.Reader.TryRead(out var next))
                {
                    lastSequence = this.Apply(next);
                    remaining--;
                }

                this.Publish(lastSequence);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref this.projectorException, exception);
            this.SignalProjectionAdvanced();
        }
    }

    private long Apply(ReadModelUpdate update)
    {
        switch (update)
        {
            case RecordWorkerUpdate worker:
                this.state.RecordWorker(worker.Worker);
                break;
            case RecordIterationUpdate iteration:
                this.state.RecordIteration(iteration.Iteration);
                break;
            case ForgetIterationUpdate iteration:
                this.state.ForgetIteration(iteration.Iteration);
                break;
            case ForgetWorkerUpdate worker:
                this.state.ForgetWorker(worker.WorkerId);
                break;
            case ClearReadModelUpdate:
                this.state.Clear();
                break;
        }

        return update.Sequence;
    }

    private void Publish(long sequence)
    {
        Volatile.Write(ref this.snapshot, this.state.ToSnapshot());
        Volatile.Write(ref this.appliedSequence, sequence);
        this.SignalProjectionAdvanced();
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

    private abstract record ReadModelUpdate(long Sequence);

    private sealed record RecordWorkerUpdate(long Sequence, WorkerReadModelWorker Worker) : ReadModelUpdate(Sequence);

    private sealed record RecordIterationUpdate(long Sequence, WorkerReadModelIterationUpdate Iteration) : ReadModelUpdate(Sequence);

    private sealed record ForgetIterationUpdate(long Sequence, WorkerIterationReference Iteration) : ReadModelUpdate(Sequence);

    private sealed record ForgetWorkerUpdate(long Sequence, WorkerId WorkerId) : ReadModelUpdate(Sequence);

    private sealed record ClearReadModelUpdate(long Sequence) : ReadModelUpdate(Sequence);
}

internal sealed class WorkSystemReadModelState
{
    private readonly Dictionary<WorkerId, WorkerReadModelWorker> workers = [];
    private readonly Dictionary<WorkerIterationReference, WorkerReadModelIteration> iterations = [];

    public void RecordWorker(WorkerReadModelWorker worker)
        => this.workers[worker.Id] = worker;

    public void RecordIteration(WorkerReadModelIterationUpdate iteration)
    {
        this.workers[iteration.Worker.Id] = iteration.Worker;
        this.iterations[iteration.Iteration.Reference] = iteration.Iteration;
    }

    public void ForgetIteration(WorkerIterationReference iteration)
        => this.iterations.Remove(iteration);

    public void ForgetWorker(WorkerId workerId)
    {
        this.workers.Remove(workerId);
        foreach (var reference in this.iterations.Values
            .Where(iteration => iteration.WorkerId == workerId)
            .Select(iteration => iteration.Reference)
            .ToArray())
        {
            this.iterations.Remove(reference);
        }
    }

    public void Clear()
    {
        this.workers.Clear();
        this.iterations.Clear();
    }

    public WorkSystemReadModelSnapshot ToSnapshot()
        => new(
            this.workers.ToDictionary(entry => entry.Key, entry => entry.Value),
            this.iterations.ToDictionary(entry => entry.Key, entry => entry.Value));
}

internal sealed class WorkSystemReadModelSnapshot
{
    public static WorkSystemReadModelSnapshot Empty { get; } = new(
        new Dictionary<WorkerId, WorkerReadModelWorker>(),
        new Dictionary<WorkerIterationReference, WorkerReadModelIteration>());

    public WorkSystemReadModelSnapshot(
        IReadOnlyDictionary<WorkerId, WorkerReadModelWorker> workersById,
        IReadOnlyDictionary<WorkerIterationReference, WorkerReadModelIteration> iterationsByReference)
    {
        this.WorkersById = workersById;
        this.IterationsByReference = iterationsByReference;
        this.Workers = [.. workersById.Values];
        this.Iterations = [.. iterationsByReference.Values];
        this.WorkersByDefinition = GroupBy(this.Workers, worker => worker.DefinitionId);
        this.WorkersByState = GroupBy(this.Workers, worker => worker.State);
        this.WorkersByDefinitionAndState = GroupBy(this.Workers, worker => (worker.DefinitionId, worker.State));
        this.WorkersBySubject = GroupNullable(this.Workers, worker => worker.SubjectId);
        this.WorkersByDefinitionAndSubject = GroupNullable<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), WorkerReadModelWorker>(
            this.Workers,
            worker => worker.SubjectId is { } subjectId ? (worker.DefinitionId, subjectId) : null);
        this.WorkersByConcurrencyKey = GroupNullable(this.Workers, worker => worker.ConcurrencyKey);
        this.WorkersByDefinitionAndConcurrencyKey = GroupNullable<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), WorkerReadModelWorker>(
            this.Workers,
            worker => worker.ConcurrencyKey is { } concurrencyKey ? (worker.DefinitionId, concurrencyKey) : null);
        this.WorkersByIdentifier = GroupMany(this.Workers, worker => worker.Identifiers);
        this.WorkersByRecurrenceEnabled = GroupBy(this.Workers, worker => worker.RecurrenceEnabled);
        this.WorkersByConcurrencyEnabled = GroupBy(this.Workers, worker => worker.ConcurrencyEnabled);
        this.WorkersByProfilingEnabled = GroupBy(this.Workers, worker => worker.ProfilingEnabled);
        this.IterationsByWorker = GroupBy(this.Iterations, iteration => iteration.WorkerId);
        this.IterationsByDefinition = GroupBy(this.Iterations, iteration => iteration.DefinitionId);
        this.IterationsByStatus = GroupBy(this.Iterations, iteration => iteration.Status);
        this.IterationsByDefinitionAndStatus = GroupBy(this.Iterations, iteration => (iteration.DefinitionId, iteration.Status));
        this.IterationsBySubject = GroupNullable(this.Iterations, iteration => iteration.SubjectId);
        this.IterationsByConcurrencyKey = GroupNullable(this.Iterations, iteration => iteration.ConcurrencyKey);
        this.IterationsByIdentifier = GroupMany(this.Iterations, iteration => iteration.Identifiers);
        this.WorkerKeys = [.. this.Workers.SelectMany(WorkerReadModelKey.From)];
        this.IterationKeys = [.. this.Iterations.SelectMany(WorkerIterationReadModelKey.From)];
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

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> GroupBy<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, TKey> getKey)
        where TKey : notnull
        => Freeze(values
            .GroupBy(getKey)
            .ToDictionary(group => group.Key, group => group.ToList()));

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> GroupNullable<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, TKey?> getKey)
        where TKey : struct
    {
        var groups = new Dictionary<TKey, List<TValue>>();
        foreach (var value in values)
        {
            if (getKey(value) is { } key)
            {
                Add(groups, key, value);
            }
        }

        return Freeze(groups);
    }

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> GroupMany<TKey, TValue>(
        IEnumerable<TValue> values,
        Func<TValue, IEnumerable<TKey>> getKeys)
        where TKey : notnull
    {
        var groups = new Dictionary<TKey, List<TValue>>();
        foreach (var value in values)
        {
            foreach (var key in getKeys(value))
            {
                Add(groups, key, value);
            }
        }

        return Freeze(groups);
    }

    private static void Add<TKey, TValue>(
        Dictionary<TKey, List<TValue>> groups,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var values))
        {
            values = [];
            groups[key] = values;
        }

        values.Add(value);
    }

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> Freeze<TKey, TValue>(
        Dictionary<TKey, List<TValue>> groups)
        where TKey : notnull
        => groups.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<TValue>)entry.Value.ToArray());
}

internal sealed record WorkerReadModelWorker(
    WorkerSnapshot Snapshot,
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

    public string Category => this.Overview.Category;

    public WorkerState State => this.Overview.State;

    public DateTimeOffset CreatedAt => this.Overview.CreatedAt;

    public DateTimeOffset StateChangedAt => this.Overview.StateChangedAt;

    public DateTimeOffset UpdatedAt => this.Overview.UpdatedAt;

    public static WorkerReadModelWorker From(WorkerSnapshot snapshot)
        => new(
            snapshot,
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
}

internal sealed record WorkerReadModelIteration(
    WorkerIterationReference Reference,
    WorkerIterationSnapshot Snapshot,
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

    public static WorkerReadModelIteration From(WorkerSnapshot worker, WorkerIterationSnapshot iteration)
    {
        var reference = new WorkerIterationReference(worker.Id, iteration.Sequence);
        return new(
            reference,
            iteration,
            new WorkerIterationOverviewItem(
                worker.Id,
                iteration.Sequence,
                worker.DefinitionId,
                worker.DefinitionName,
                worker.DefinitionCategory,
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
    WorkerReadModelIteration Iteration);

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
