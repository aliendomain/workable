using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkerIndex
{
    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<WorkerId, byte>> byDefinition = [];
    private readonly ConcurrentDictionary<WorkSubjectId, ConcurrentDictionary<WorkerId, byte>> bySubject = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndSubject = [];
    private readonly ConcurrentDictionary<WorkConcurrencyKey, ConcurrentDictionary<WorkerId, byte>> byConcurrencyKey = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndConcurrencyKey = [];
    private readonly ConcurrentDictionary<WorkIdentifier, ConcurrentDictionary<WorkerId, byte>> byIdentifier = [];
    private readonly ConcurrentDictionary<WorkerState, ConcurrentDictionary<WorkerId, byte>> byState = [];
    private readonly ConcurrentDictionary<bool, ConcurrentDictionary<WorkerId, byte>> byRecurrenceEnabled = [];
    private readonly ConcurrentDictionary<bool, ConcurrentDictionary<WorkerId, byte>> byConcurrencyEnabled = [];
    private readonly ConcurrentDictionary<bool, ConcurrentDictionary<WorkerId, byte>> byProfilingEnabled = [];
    private readonly ConcurrentDictionary<WorkerState, int> stateCounts = [];
    private readonly ConcurrentDictionary<WorkDefinitionId, int> activeOrQueuedDefinitionCounts = [];
    private readonly ConcurrentDictionary<string, int> keyTypeCounts = [];
    private readonly ConcurrentDictionary<IndexedWorkKeyTypeKind, int> keyTypeKindCounts = [];
    private readonly ConcurrentDictionary<string, string> keyTypeDisplayNames = [];
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
                this.RemoveState(existing.State, worker.Id);
                this.AddState(current.State, worker.Id);
                this.SynchronizeActiveOrQueuedDefinitionCount(existing, current);
            }

            if (existing.RecurrenceEnabled != current.RecurrenceEnabled)
            {
                Remove(this.byRecurrenceEnabled, existing.RecurrenceEnabled, worker.Id);
                Add(this.byRecurrenceEnabled, current.RecurrenceEnabled, worker.Id);
            }

            if (existing.ConcurrencyEnabled != current.ConcurrencyEnabled)
            {
                Remove(this.byConcurrencyEnabled, existing.ConcurrencyEnabled, worker.Id);
                Add(this.byConcurrencyEnabled, current.ConcurrencyEnabled, worker.Id);
            }

            if (existing.ProfilingEnabled != current.ProfilingEnabled)
            {
                Remove(this.byProfilingEnabled, existing.ProfilingEnabled, worker.Id);
                Add(this.byProfilingEnabled, current.ProfilingEnabled, worker.Id);
            }

            this.keysByWorker[worker.Id] = current with
            {
                State = current.State,
            };
        }
    }

    private void AddAll(WorkerIndexKeys keys, WorkerId workerId)
    {
        Add(this.byDefinition, keys.DefinitionId, workerId);
        this.AddState(keys.State, workerId);
        this.AddActiveOrQueuedDefinitionCount(keys);
        Add(this.byRecurrenceEnabled, keys.RecurrenceEnabled, workerId);
        Add(this.byConcurrencyEnabled, keys.ConcurrencyEnabled, workerId);
        Add(this.byProfilingEnabled, keys.ProfilingEnabled, workerId);

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

        this.AddKeyTypeCounts(keys);
    }

    public void AddIdentifier(WorkerRecord worker, WorkIdentifier identifier)
    {
        lock (this.sync)
        {
            if (this.keysByWorker.TryGetValue(worker.Id, out var existing))
            {
                if (existing.Identifiers.Contains(identifier))
                {
                    return;
                }

                this.keysByWorker[worker.Id] = existing with
                {
                    Identifiers = existing.Identifiers.Append(identifier).ToHashSet(),
                };

                this.AddIdentifierKeyTypeCounts(existing, identifier);
            }
            else
            {
                var keys = WorkerIndexKeys.From(worker);
                this.keysByWorker[worker.Id] = keys;
                this.AddKeyTypeCounts(keys);
            }

            Add(this.byIdentifier, identifier, worker.Id);
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
            this.byConcurrencyKey.Clear();
            this.byDefinitionAndConcurrencyKey.Clear();
            this.byIdentifier.Clear();
            this.byState.Clear();
            this.byRecurrenceEnabled.Clear();
            this.byConcurrencyEnabled.Clear();
            this.byProfilingEnabled.Clear();
            this.stateCounts.Clear();
            this.activeOrQueuedDefinitionCounts.Clear();
            this.keyTypeCounts.Clear();
            this.keyTypeKindCounts.Clear();
            this.keyTypeDisplayNames.Clear();
            this.keysByWorker.Clear();
        }
    }

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

        if (query.Configuration is { } configuration)
        {
            AddIfPresent(candidates, this.byRecurrenceEnabled, configuration.RecurrenceEnabled);
            AddIfPresent(candidates, this.byConcurrencyEnabled, configuration.ConcurrencyEnabled);
            AddIfPresent(candidates, this.byProfilingEnabled, configuration.ProfilingEnabled);
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

    public IEnumerable<IndexedWorkKey> WorkKeys()
        => EnumerateKeys(this.bySubject, WorkKeyKind.Subject)
            .Concat(EnumerateKeys(this.byConcurrencyKey, WorkKeyKind.ConcurrencyKey))
            .Concat(EnumerateKeys(this.byIdentifier, WorkKeyKind.Identifier));

    public IReadOnlyList<IndexedWorkKeyTypeFacet> CommonKeyTypes(int take)
    {
        var normalizedTake = Math.Max(0, take);
        if (normalizedTake == 0)
        {
            return [];
        }

        return [.. this.keyTypeCounts
            .Where(count => count.Value > 0)
            .Select(count => new IndexedWorkKeyTypeFacet(
                this.keyTypeDisplayNames.GetValueOrDefault(count.Key, count.Key),
                count.Value,
                this.CountByKind(count.Key)))
            .OrderByDescending(facet => facet.WorkerCount)
            .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedTake)];
    }

    public IEnumerable<WorkerId> ByState(WorkerState state)
        => this.byState.TryGetValue(state, out var workerIds) ? workerIds.Keys : [];

    public IReadOnlyDictionary<WorkerState, int> CountByState()
        => this.stateCounts
            .Where(count => count.Value > 0)
            .ToDictionary(count => count.Key, count => count.Value);

    public int ActiveOrQueuedDefinitionCount()
        => this.activeOrQueuedDefinitionCounts.Count(count => count.Value > 0);

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
        lock (this.sync)
        {
            if (!this.keysByWorker.TryRemove(workerId, out var keys))
            {
                return;
            }

            this.ForgetLocked(workerId, keys);
        }
    }

    private void ForgetLocked(WorkerId workerId, WorkerIndexKeys keys)
    {
        Remove(this.byDefinition, keys.DefinitionId, workerId);
        this.RemoveState(keys.State, workerId);
        this.RemoveActiveOrQueuedDefinitionCount(keys);
        Remove(this.byRecurrenceEnabled, keys.RecurrenceEnabled, workerId);
        Remove(this.byConcurrencyEnabled, keys.ConcurrencyEnabled, workerId);
        Remove(this.byProfilingEnabled, keys.ProfilingEnabled, workerId);

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

        this.RemoveKeyTypeCounts(keys);
    }

    private void AddState(WorkerState state, WorkerId workerId)
    {
        Add(this.byState, state, workerId);
        this.stateCounts.AddOrUpdate(state, 1, static (_, count) => count + 1);
    }

    private void RemoveState(WorkerState state, WorkerId workerId)
    {
        if (!Remove(this.byState, state, workerId))
        {
            return;
        }

        this.stateCounts.AddOrUpdate(state, 0, static (_, count) => Math.Max(0, count - 1));
    }

    private void SynchronizeActiveOrQueuedDefinitionCount(WorkerIndexKeys existing, WorkerIndexKeys current)
    {
        if (CountsTowardActiveOrQueuedDefinition(existing.State) == CountsTowardActiveOrQueuedDefinition(current.State))
        {
            return;
        }

        this.RemoveActiveOrQueuedDefinitionCount(existing);
        this.AddActiveOrQueuedDefinitionCount(current);
    }

    private void AddActiveOrQueuedDefinitionCount(WorkerIndexKeys keys)
    {
        if (CountsTowardActiveOrQueuedDefinition(keys.State))
        {
            this.activeOrQueuedDefinitionCounts.AddOrUpdate(keys.DefinitionId, 1, static (_, count) => count + 1);
        }
    }

    private void RemoveActiveOrQueuedDefinitionCount(WorkerIndexKeys keys)
    {
        if (CountsTowardActiveOrQueuedDefinition(keys.State))
        {
            this.activeOrQueuedDefinitionCounts.AddOrUpdate(keys.DefinitionId, 0, static (_, count) => Math.Max(0, count - 1));
        }
    }

    private void AddIdentifierKeyTypeCounts(WorkerIndexKeys existing, WorkIdentifier identifier)
    {
        if (!HasType(existing.KeyTypes(), identifier.Type))
        {
            this.AddKeyTypeCount(identifier.Type);
        }

        if (!HasKindType(existing.KindTypes(), WorkKeyKind.Identifier, identifier.Type))
        {
            this.AddKeyTypeKindCount(WorkKeyKind.Identifier, identifier.Type);
        }
    }

    private void AddKeyTypeCounts(WorkerIndexKeys keys)
    {
        foreach (var type in keys.KeyTypes())
        {
            this.AddKeyTypeCount(type);
        }

        foreach (var kindType in keys.KindTypes())
        {
            this.AddKeyTypeKindCount(kindType.Kind, kindType.Type);
        }
    }

    private void RemoveKeyTypeCounts(WorkerIndexKeys keys)
    {
        foreach (var type in keys.KeyTypes())
        {
            this.RemoveKeyTypeCount(type);
        }

        foreach (var kindType in keys.KindTypes())
        {
            this.RemoveKeyTypeKindCount(kindType.Kind, kindType.Type);
        }
    }

    private void AddKeyTypeCount(string type)
    {
        var normalizedType = NormalizeType(type);
        this.keyTypeDisplayNames.TryAdd(normalizedType, type);
        this.keyTypeCounts.AddOrUpdate(normalizedType, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeCount(string type)
    {
        var normalizedType = NormalizeType(type);
        this.keyTypeCounts.AddOrUpdate(normalizedType, 0, static (_, count) => Math.Max(0, count - 1));
    }

    private void AddKeyTypeKindCount(WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkKeyTypeKind(kind, NormalizeType(type));
        this.keyTypeKindCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeKindCount(WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkKeyTypeKind(kind, NormalizeType(type));
        this.keyTypeKindCounts.AddOrUpdate(key, 0, static (_, count) => Math.Max(0, count - 1));
    }

    private IReadOnlyDictionary<WorkKeyKind, int> CountByKind(string normalizedType)
        => this.keyTypeKindCounts
            .Where(count => count.Key.Type == normalizedType && count.Value > 0)
            .ToDictionary(count => count.Key.Kind, count => count.Value);

    private static void Add<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key,
        WorkerId workerId)
        where TKey : notnull
    {
        var workerIds = index.GetOrAdd(key, static _ => []);
        workerIds[workerId] = 0;
    }

    private static bool Remove<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key,
        WorkerId workerId)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var workerIds))
        {
            return false;
        }

        var removed = workerIds.TryRemove(workerId, out _);
        if (workerIds.IsEmpty)
        {
            index.TryRemove(new KeyValuePair<TKey, ConcurrentDictionary<WorkerId, byte>>(key, workerIds));
        }

        return removed;
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

    private static IEnumerable<IndexedWorkKey> EnumerateKeys<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        WorkKeyKind kind)
        where TKey : struct, IWorkKey
        => index.Select(entry => new IndexedWorkKey(kind, entry.Key.Type, entry.Key.Value, [.. entry.Value.Keys]));

    public readonly record struct IndexedWorkKey(
        WorkKeyKind Kind,
        string Type,
        string Value,
        IReadOnlyList<WorkerId> WorkerIds);

    public readonly record struct IndexedWorkKeyTypeFacet(
        string Type,
        int WorkerCount,
        IReadOnlyDictionary<WorkKeyKind, int> WorkerCountByKind);

    private readonly record struct IndexedWorkKeyTypeKind(
        WorkKeyKind Kind,
        string Type);

    private sealed record WorkerIndexKeys(
        WorkDefinitionId DefinitionId,
        WorkerState State,
        bool RecurrenceEnabled,
        bool ConcurrencyEnabled,
        bool ProfilingEnabled,
        WorkSubjectId? SubjectId,
        WorkConcurrencyKey? ConcurrencyKey,
        IReadOnlySet<WorkIdentifier> Identifiers)
    {
        public static WorkerIndexKeys From(WorkerRecord worker)
            => new(
                worker.Work.Definition.Id,
                worker.State,
                worker.Configuration.Recurrence.IsEnabled,
                worker.Configuration.Concurrency.IsEnabled,
                worker.Options.ProfilingEnabled,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers);

        public IEnumerable<string> KeyTypes()
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (this.SubjectId is { } subjectId)
            {
                types.Add(subjectId.Type);
            }

            if (this.ConcurrencyKey is { } concurrencyKey)
            {
                types.Add(concurrencyKey.Type);
            }

            foreach (var identifier in this.Identifiers)
            {
                types.Add(identifier.Type);
            }

            return types;
        }

        public IEnumerable<(WorkKeyKind Kind, string Type)> KindTypes()
        {
            var kindTypes = new HashSet<(WorkKeyKind Kind, string Type)>(KindTypeComparer.Instance);
            if (this.SubjectId is { } subjectId)
            {
                kindTypes.Add((WorkKeyKind.Subject, subjectId.Type));
            }

            if (this.ConcurrencyKey is { } concurrencyKey)
            {
                kindTypes.Add((WorkKeyKind.ConcurrencyKey, concurrencyKey.Type));
            }

            foreach (var identifier in this.Identifiers)
            {
                kindTypes.Add((WorkKeyKind.Identifier, identifier.Type));
            }

            return kindTypes;
        }
    }

    private sealed class KindTypeComparer : IEqualityComparer<(WorkKeyKind Kind, string Type)>
    {
        public static KindTypeComparer Instance { get; } = new();

        public bool Equals((WorkKeyKind Kind, string Type) x, (WorkKeyKind Kind, string Type) y)
            => x.Kind == y.Kind && string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((WorkKeyKind Kind, string Type) obj)
            => HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Type));
    }

    private static bool HasType(IEnumerable<string> types, string type)
        => types.Any(existing => string.Equals(existing, type, StringComparison.OrdinalIgnoreCase));

    private static bool HasKindType(
        IEnumerable<(WorkKeyKind Kind, string Type)> kindTypes,
        WorkKeyKind kind,
        string type)
        => kindTypes.Any(existing =>
            existing.Kind == kind &&
            string.Equals(existing.Type, type, StringComparison.OrdinalIgnoreCase));

    private static bool CountsTowardActiveOrQueuedDefinition(WorkerState state)
        => state is WorkerState.Queued
            or WorkerState.Running
            or WorkerState.Waiting
            or WorkerState.Retrying
            or WorkerState.Pausing
            or WorkerState.Canceling
            or WorkerState.Paused;

    private static string NormalizeType(string type)
        => type.ToUpperInvariant();
}
