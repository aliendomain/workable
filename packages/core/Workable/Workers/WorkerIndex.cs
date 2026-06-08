using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkerIndex
{
    // This index only supports live-worker correctness and control paths. Query-shaped
    // indexes belong to the read model so lifecycle writes stay narrowly scoped.
    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<WorkerId, byte>> byDefinition = [];
    private readonly ConcurrentDictionary<WorkSubjectId, ConcurrentDictionary<WorkerId, byte>> bySubject = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndSubject = [];
    private readonly ConcurrentDictionary<WorkerState, ConcurrentDictionary<WorkerId, byte>> byState = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkerState State), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndState = [];
    private readonly ConcurrentDictionary<WorkerId, WorkerIndexKeys> keysByWorker = [];

    public void Register(WorkerRecord worker)
    {
        var keys = WorkerIndexKeys.From(worker);
        lock (this.sync)
        {
            if (!this.keysByWorker.TryAdd(worker.Id, keys))
            {
                return;
            }

            this.AddAll(keys, worker.Id);
        }
    }

    public void Synchronize(WorkerRecord worker)
    {
        var current = WorkerIndexKeys.From(worker);
        lock (this.sync)
        {
            if (!this.keysByWorker.TryGetValue(worker.Id, out var existing))
            {
                this.AddAll(current, worker.Id);
                this.keysByWorker[worker.Id] = current;
                return;
            }

            if (existing.State != current.State)
            {
                this.RemoveState(existing.DefinitionId, existing.State, worker.Id);
                this.AddState(current.DefinitionId, current.State, worker.Id);
            }

            this.keysByWorker[worker.Id] = current;
        }
    }

    public void Forget(WorkerRecord worker)
        => this.Forget(worker.Id);

    public void Clear()
    {
        lock (this.sync)
        {
            this.byDefinition.Clear();
            this.bySubject.Clear();
            this.byDefinitionAndSubject.Clear();
            this.byState.Clear();
            this.byDefinitionAndState.Clear();
            this.keysByWorker.Clear();
        }
    }

    public IReadOnlyList<WorkerId> ByDefinition(WorkDefinitionId definitionId)
        => this.byDefinition.TryGetValue(definitionId, out var workers)
            ? [.. workers.Keys]
            : [];

    public IReadOnlyList<WorkerId> BySubject(WorkSubjectId subjectId)
        => this.bySubject.TryGetValue(subjectId, out var workers)
            ? [.. workers.Keys]
            : [];

    public IReadOnlyList<WorkerId> ByDefinitionAndSubject(WorkDefinitionId definitionId, WorkSubjectId subjectId)
        => this.byDefinitionAndSubject.TryGetValue((definitionId, subjectId), out var workers)
            ? [.. workers.Keys]
            : [];

    public IReadOnlyDictionary<WorkerState, int> CountByState(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        if (definitionIds is not { Count: > 0 })
        {
            return this.byState
                .Select(entry => new KeyValuePair<WorkerState, int>(entry.Key, entry.Value.Count))
                .Where(entry => entry.Value > 0)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        var counts = new Dictionary<WorkerState, int>();
        foreach (var entry in this.byDefinitionAndState)
        {
            if (!definitionIds.Contains(entry.Key.DefinitionId) || entry.Value.Count == 0)
            {
                continue;
            }

            counts[entry.Key.State] = counts.GetValueOrDefault(entry.Key.State) + entry.Value.Count;
        }

        return counts;
    }

    private void AddAll(WorkerIndexKeys keys, WorkerId workerId)
    {
        Add(this.byDefinition, keys.DefinitionId, workerId);
        this.AddState(keys.DefinitionId, keys.State, workerId);

        if (keys.SubjectId is { } subjectId)
        {
            Add(this.bySubject, subjectId, workerId);
            Add(this.byDefinitionAndSubject, (keys.DefinitionId, subjectId), workerId);
        }
    }

    private void Forget(WorkerId workerId)
    {
        lock (this.sync)
        {
            if (this.keysByWorker.TryRemove(workerId, out var keys))
            {
                this.ForgetLocked(workerId, keys);
            }
        }
    }

    private void ForgetLocked(WorkerId workerId, WorkerIndexKeys keys)
    {
        Remove(this.byDefinition, keys.DefinitionId, workerId);
        this.RemoveState(keys.DefinitionId, keys.State, workerId);

        if (keys.SubjectId is { } subjectId)
        {
            Remove(this.bySubject, subjectId, workerId);
            Remove(this.byDefinitionAndSubject, (keys.DefinitionId, subjectId), workerId);
        }
    }

    private void AddState(WorkDefinitionId definitionId, WorkerState state, WorkerId workerId)
    {
        Add(this.byState, state, workerId);
        Add(this.byDefinitionAndState, (definitionId, state), workerId);
    }

    private void RemoveState(WorkDefinitionId definitionId, WorkerState state, WorkerId workerId)
    {
        Remove(this.byState, state, workerId);
        Remove(this.byDefinitionAndState, (definitionId, state), workerId);
    }

    private static void Add<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key,
        WorkerId workerId)
        where TKey : notnull
    {
        var workers = index.GetOrAdd(key, static _ => []);
        workers[workerId] = 0;
    }

    private static void Remove<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key,
        WorkerId workerId)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var workers))
        {
            return;
        }

        workers.TryRemove(workerId, out _);
        if (workers.IsEmpty)
        {
            index.TryRemove(new KeyValuePair<TKey, ConcurrentDictionary<WorkerId, byte>>(key, workers));
        }
    }

    private sealed record WorkerIndexKeys(
        WorkDefinitionId DefinitionId,
        WorkerState State,
        WorkSubjectId? SubjectId)
    {
        public static WorkerIndexKeys From(WorkerRecord worker)
            => new(
                worker.Work.Definition.Id,
                worker.State,
                worker.SubjectId);
    }
}
