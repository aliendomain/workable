using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkerIndex
{
    private readonly ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<WorkerId, byte>> byDefinition = [];
    private readonly ConcurrentDictionary<WorkSubjectId, ConcurrentDictionary<WorkerId, byte>> bySubject = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndSubject = [];
    private readonly ConcurrentDictionary<WorkConcurrencyKey, ConcurrentDictionary<WorkerId, byte>> byConcurrencyKey = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndConcurrencyKey = [];
    private readonly ConcurrentDictionary<WorkIdentifier, ConcurrentDictionary<WorkerId, byte>> byIdentifier = [];
    private readonly ConcurrentDictionary<WorkerState, ConcurrentDictionary<WorkerId, byte>> byState = [];
    private readonly ConcurrentDictionary<WorkerId, WorkerIndexKeys> keysByWorker = [];

    public void Register(WorkerRecord worker)
    {
        var keys = WorkerIndexKeys.From(worker);
        if (!this.keysByWorker.TryAdd(worker.Id, keys))
        {
            return;
        }

        this.AddAll(keys, worker.Id);
    }

    public void Synchronize(WorkerRecord worker)
    {
        var current = WorkerIndexKeys.From(worker);
        this.keysByWorker.AddOrUpdate(
            worker.Id,
            _ =>
            {
                this.AddAll(current, worker.Id);
                return current;
            },
            (_, existing) =>
            {
                if (existing.State != current.State)
                {
                    Remove(this.byState, existing.State, worker.Id);
                    Add(this.byState, current.State, worker.Id);
                }

                return existing with
                {
                    State = current.State,
                };
            });
    }

    private void AddAll(WorkerIndexKeys keys, WorkerId workerId)
    {
        Add(this.byDefinition, keys.DefinitionId, workerId);
        Add(this.byState, keys.State, workerId);

        if (keys.SubjectId is { } subjectId)
        {
            Add(this.bySubject, subjectId, workerId);
            Add(this.byDefinitionAndSubject, (keys.DefinitionId, subjectId), workerId);
        }

        if (keys.ConcurrencyKey is { } concurrencyKey)
        {
            Add(this.byConcurrencyKey, concurrencyKey, workerId);
            Add(this.byDefinitionAndConcurrencyKey, (keys.DefinitionId, concurrencyKey), workerId);
        }

        foreach (var identifier in keys.Identifiers)
        {
            Add(this.byIdentifier, identifier, workerId);
        }
    }

    public void AddIdentifier(WorkerRecord worker, WorkIdentifier identifier)
    {
        this.keysByWorker.AddOrUpdate(
            worker.Id,
            _ => WorkerIndexKeys.From(worker),
            (_, existing) => existing with
            {
                Identifiers = existing.Identifiers.Append(identifier).ToHashSet(),
            });
        Add(this.byIdentifier, identifier, worker.Id);
    }

    public void Forget(WorkerRecord worker)
        => this.Forget(worker.Id);

    public IReadOnlySet<WorkerId>? FindBestCandidates(WorkerQuery query)
    {
        var candidates = new List<IReadOnlyCollection<WorkerId>>();

        if (query.DefinitionId is { } definitionId &&
            query.SubjectId is { } subjectId &&
            this.byDefinitionAndSubject.TryGetValue((definitionId, subjectId), out var definitionSubject))
        {
            candidates.Add([.. definitionSubject.Keys]);
        }

        if (query.DefinitionId is { } definitionForKey &&
            query.ConcurrencyKey is { } concurrencyKey &&
            this.byDefinitionAndConcurrencyKey.TryGetValue((definitionForKey, concurrencyKey), out var definitionConcurrencyKey))
        {
            candidates.Add([.. definitionConcurrencyKey.Keys]);
        }

        if (query.States is { } states)
        {
            candidates.Add(this.ByStates(states));
        }

        AddIfPresent(candidates, this.byIdentifier, query.Identifier);
        AddIfPresent(candidates, this.bySubject, query.SubjectId);
        AddIfPresent(candidates, this.byConcurrencyKey, query.ConcurrencyKey);
        AddIfPresent(candidates, this.byDefinition, query.DefinitionId);

        return candidates.Count == 0
            ? null
            : candidates.MinBy(candidate => candidate.Count)?.ToHashSet();
    }

    public IReadOnlyList<WorkerId> ByDefinition(WorkDefinitionId definitionId)
        => this.byDefinition.TryGetValue(definitionId, out var workerIds) ? [.. workerIds.Keys] : [];

    public IReadOnlyList<WorkerId> BySubject(WorkSubjectId subjectId)
        => this.bySubject.TryGetValue(subjectId, out var workerIds) ? [.. workerIds.Keys] : [];

    public IReadOnlyList<WorkerId> ByDefinitionAndSubject(WorkDefinitionId definitionId, WorkSubjectId subjectId)
        => this.byDefinitionAndSubject.TryGetValue((definitionId, subjectId), out var workerIds) ? [.. workerIds.Keys] : [];

    public IReadOnlyList<WorkerId> ByStates(IReadOnlySet<WorkerState> states)
    {
        if (states.Count == 0)
        {
            return [];
        }

        var workerIds = new HashSet<WorkerId>();
        foreach (var state in states)
        {
            if (!this.byState.TryGetValue(state, out var stateWorkerIds))
            {
                continue;
            }

            foreach (var workerId in stateWorkerIds.Keys)
            {
                workerIds.Add(workerId);
            }
        }

        return [.. workerIds];
    }

    private void Forget(WorkerId workerId)
    {
        if (!this.keysByWorker.TryRemove(workerId, out var keys))
        {
            return;
        }

        Remove(this.byDefinition, keys.DefinitionId, workerId);
        Remove(this.byState, keys.State, workerId);

        if (keys.SubjectId is { } subjectId)
        {
            Remove(this.bySubject, subjectId, workerId);
            Remove(this.byDefinitionAndSubject, (keys.DefinitionId, subjectId), workerId);
        }

        if (keys.ConcurrencyKey is { } concurrencyKey)
        {
            Remove(this.byConcurrencyKey, concurrencyKey, workerId);
            Remove(this.byDefinitionAndConcurrencyKey, (keys.DefinitionId, concurrencyKey), workerId);
        }

        foreach (var identifier in keys.Identifiers)
        {
            Remove(this.byIdentifier, identifier, workerId);
        }
    }

    private static void Add<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key,
        WorkerId workerId)
        where TKey : notnull
    {
        var workerIds = index.GetOrAdd(key, static _ => []);
        workerIds[workerId] = 0;
    }

    private static void Remove<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key,
        WorkerId workerId)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var workerIds))
        {
            return;
        }

        workerIds.TryRemove(workerId, out _);
        if (workerIds.IsEmpty)
        {
            index.TryRemove(new KeyValuePair<TKey, ConcurrentDictionary<WorkerId, byte>>(key, workerIds));
        }
    }

    private static void AddIfPresent<TKey>(
        List<IReadOnlyCollection<WorkerId>> candidates,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey? key)
        where TKey : struct
    {
        if (key is { } requiredKey && index.TryGetValue(requiredKey, out var workerIds))
        {
            candidates.Add([.. workerIds.Keys]);
        }
    }

    private sealed record WorkerIndexKeys(
        WorkDefinitionId DefinitionId,
        WorkerState State,
        WorkSubjectId? SubjectId,
        WorkConcurrencyKey? ConcurrencyKey,
        IReadOnlySet<WorkIdentifier> Identifiers)
    {
        public static WorkerIndexKeys From(WorkerRecord worker)
            => new(
                worker.Work.Definition.Id,
                worker.State,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers);
    }
}
