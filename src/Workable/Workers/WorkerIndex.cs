using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkerIndex
{
    private static readonly WorkerState[] ActiveOrQueuedDefinitionStates =
    [
        WorkerState.Queued,
        WorkerState.Running,
        WorkerState.Waiting,
        WorkerState.Retrying,
        WorkerState.Pausing,
        WorkerState.Canceling,
        WorkerState.Paused,
    ];

    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<WorkerId, byte>> byDefinition = [];
    private readonly ConcurrentDictionary<WorkSubjectId, ConcurrentDictionary<WorkerId, byte>> bySubject = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkSubjectId SubjectId), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndSubject = [];
    private readonly ConcurrentDictionary<WorkConcurrencyKey, ConcurrentDictionary<WorkerId, byte>> byConcurrencyKey = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkConcurrencyKey ConcurrencyKey), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndConcurrencyKey = [];
    private readonly ConcurrentDictionary<WorkIdentifier, ConcurrentDictionary<WorkerId, byte>> byIdentifier = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IndexedWorkKeyReference, byte>> keysByType = [];
    private readonly ConcurrentDictionary<(WorkKeyKind Kind, string Type), ConcurrentDictionary<IndexedWorkKeyReference, byte>> keysByKindAndType = [];
    private readonly ConcurrentDictionary<WorkerState, ConcurrentDictionary<WorkerId, byte>> byState = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkerState State), ConcurrentDictionary<WorkerId, byte>> byDefinitionAndState = [];
    private readonly ConcurrentDictionary<bool, ConcurrentDictionary<WorkerId, byte>> byRecurrenceEnabled = [];
    private readonly ConcurrentDictionary<bool, ConcurrentDictionary<WorkerId, byte>> byConcurrencyEnabled = [];
    private readonly ConcurrentDictionary<bool, ConcurrentDictionary<WorkerId, byte>> byProfilingEnabled = [];
    private readonly SortedSet<QueuedWorkerIndexEntry> queuedWorkers = [];
    private readonly Dictionary<WorkDefinitionId, SortedSet<QueuedWorkerIndexEntry>> queuedWorkersByDefinition = [];
    private readonly ConcurrentDictionary<string, int> keyTypeCounts = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, string Type), int> keyTypeCountsByDefinition = [];
    private readonly ConcurrentDictionary<IndexedWorkKeyTypeKind, int> keyTypeKindCounts = [];
    private readonly ConcurrentDictionary<IndexedWorkDefinitionKeyTypeKind, int> keyTypeKindCountsByDefinition = [];
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
                this.RemoveState(existing.DefinitionId, existing.State, worker.Id);
                this.AddState(current.DefinitionId, current.State, worker.Id);
            }

            if (existing.State == WorkerState.Queued &&
                (current.State != WorkerState.Queued || existing.StateChangedAt != current.StateChangedAt))
            {
                this.RemoveQueued(existing, worker.Id);
            }

            if (current.State == WorkerState.Queued &&
                (existing.State != WorkerState.Queued || existing.StateChangedAt != current.StateChangedAt))
            {
                this.AddQueued(current, worker.Id);
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

            this.keysByWorker[worker.Id] = current;
        }
    }

    private void AddAll(WorkerIndexKeys keys, WorkerId workerId)
    {
        Add(this.byDefinition, keys.DefinitionId, workerId);
        this.AddState(keys.DefinitionId, keys.State, workerId);
        this.AddQueued(keys, workerId);
        Add(this.byRecurrenceEnabled, keys.RecurrenceEnabled, workerId);
        Add(this.byConcurrencyEnabled, keys.ConcurrencyEnabled, workerId);
        Add(this.byProfilingEnabled, keys.ProfilingEnabled, workerId);

        if (keys.SubjectId is { } subjectId)
        {
            Add(this.bySubject, subjectId, workerId);
            this.AddKeyReference(WorkKeyKind.Subject, subjectId.Type, subjectId.Value);
            Add(this.byDefinitionAndSubject, (keys.DefinitionId, subjectId), workerId);
        }

        if (keys.ConcurrencyKey is { } concurrencyKey)
        {
            Add(this.byConcurrencyKey, concurrencyKey, workerId);
            this.AddKeyReference(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value);
            Add(this.byDefinitionAndConcurrencyKey, (keys.DefinitionId, concurrencyKey), workerId);
        }

        foreach (var identifier in keys.Identifiers)
        {
            Add(this.byIdentifier, identifier, workerId);
            this.AddKeyReference(WorkKeyKind.Identifier, identifier.Type, identifier.Value);
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
            this.AddKeyReference(WorkKeyKind.Identifier, identifier.Type, identifier.Value);
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
            this.keysByType.Clear();
            this.keysByKindAndType.Clear();
            this.byState.Clear();
            this.byDefinitionAndState.Clear();
            this.byRecurrenceEnabled.Clear();
            this.byConcurrencyEnabled.Clear();
            this.byProfilingEnabled.Clear();
            this.queuedWorkers.Clear();
            this.queuedWorkersByDefinition.Clear();
            this.keyTypeCounts.Clear();
            this.keyTypeCountsByDefinition.Clear();
            this.keyTypeKindCounts.Clear();
            this.keyTypeKindCountsByDefinition.Clear();
            this.keyTypeDisplayNames.Clear();
            this.keysByWorker.Clear();
        }
    }

    public IReadOnlySet<WorkerId>? FindBestCandidates(
        WorkerCriteria query,
        IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        if (definitionIds is { Count: 0 })
        {
            return new HashSet<WorkerId>();
        }

        var candidates = new List<CandidateSet<WorkerId>>();

        if (query.DefinitionId is { } definitionId &&
            query.SubjectId is { } subjectId &&
            !TryAddCandidate(candidates, this.byDefinitionAndSubject, (definitionId, subjectId)))
        {
            return new HashSet<WorkerId>();
        }

        if (query.DefinitionId is { } definitionForKey &&
            query.ConcurrencyKey is { } concurrencyKey &&
            !TryAddCandidate(candidates, this.byDefinitionAndConcurrencyKey, (definitionForKey, concurrencyKey)))
        {
            return new HashSet<WorkerId>();
        }

        if (definitionIds is { Count: > 0 })
        {
            var definitionCandidates = this.GetDefinitionCandidates(definitionIds, query.States);
            if (definitionCandidates.Count == 0)
            {
                return new HashSet<WorkerId>();
            }

            candidates.Add(definitionCandidates);
        }

        if (query.States is { } states)
        {
            var stateCandidates = this.ByStates(states);
            if (stateCandidates.Count == 0)
            {
                return new HashSet<WorkerId>();
            }

            candidates.Add(new CandidateSet<WorkerId>(stateCandidates.Count, stateCandidates));
        }

        if (query.Configuration is { } configuration)
        {
            if (!AddIfPresent(candidates, this.byRecurrenceEnabled, configuration.RecurrenceEnabled) ||
                !AddIfPresent(candidates, this.byConcurrencyEnabled, configuration.ConcurrencyEnabled) ||
                !AddIfPresent(candidates, this.byProfilingEnabled, configuration.ProfilingEnabled))
            {
                return new HashSet<WorkerId>();
            }
        }

        if (!AddIfPresent(candidates, this.byIdentifier, query.Identifier) ||
            !AddIfPresent(candidates, this.bySubject, query.SubjectId) ||
            !AddIfPresent(candidates, this.byConcurrencyKey, query.ConcurrencyKey) ||
            !AddIfPresent(candidates, this.byDefinition, query.DefinitionId))
        {
            return new HashSet<WorkerId>();
        }

        return candidates.Count == 0
            ? null
            : candidates.MinBy(candidate => candidate.Count).Values.ToHashSet();
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

    public IEnumerable<IndexedWorkKey> WorkKeys(WorkKeyKind? kind, string? type, string? value)
    {
        if (kind is { } requiredKind &&
            !string.IsNullOrWhiteSpace(type) &&
            !string.IsNullOrWhiteSpace(value))
        {
            var normalizedType = NormalizeType(type);
            return this.keysByKindAndType.TryGetValue((requiredKind, normalizedType), out var references)
                ? references.Keys
                    .Where(reference => string.Equals(reference.Value, value, StringComparison.OrdinalIgnoreCase))
                    .Select(this.ToWorkKey)
                    .OfType<IndexedWorkKey>()
                : [];
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            var normalizedType = NormalizeType(type);
            var references = kind is { } typeKind
                ? this.keysByKindAndType.TryGetValue((typeKind, normalizedType), out var typedKindReferences) ? typedKindReferences.Keys : []
                : this.keysByType.TryGetValue(normalizedType, out var typedReferences) ? typedReferences.Keys : [];
            return references.Select(this.ToWorkKey).OfType<IndexedWorkKey>();
        }

        return kind is null
            ? this.WorkKeys()
            : this.WorkKeysByKind(kind.Value);
    }

    public IReadOnlyList<IndexedWorkKeyTypeFacet> CommonKeyTypes(
        int take,
        IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        var normalizedTake = Math.Max(0, take);
        if (normalizedTake == 0)
        {
            return [];
        }

        if (definitionIds is { Count: 0 })
        {
            return [];
        }

        if (definitionIds is not null)
        {
            return [.. this.keyTypeCountsByDefinition
                .Where(count => definitionIds.Contains(count.Key.DefinitionId) && count.Value > 0)
                .GroupBy(count => count.Key.Type)
                .Select(group => new IndexedWorkKeyTypeFacet(
                    this.keyTypeDisplayNames.GetValueOrDefault(group.Key, group.Key),
                    group.Sum(count => count.Value),
                    this.CountByKind(group.Key, definitionIds)))
                .OrderByDescending(facet => facet.WorkerCount)
                .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)
                .Take(normalizedTake)];
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

    public IReadOnlyList<WorkerId> ByState(WorkerState state, IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        if (definitionIds is null)
        {
            return [.. this.ByState(state)];
        }

        if (definitionIds.Count == 0)
        {
            return [];
        }

        var workerIds = new HashSet<WorkerId>();
        foreach (var definitionId in definitionIds)
        {
            if (!this.byDefinitionAndState.TryGetValue((definitionId, state), out var stateWorkerIds))
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

    public IReadOnlyDictionary<WorkerState, int> CountByState()
        => this.byState
            .Select(count => new KeyValuePair<WorkerState, int>(count.Key, count.Value.Count))
            .Where(count => count.Value > 0)
            .ToDictionary(count => count.Key, count => count.Value);

    public IReadOnlyDictionary<WorkerState, int> CountByState(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        if (definitionIds is null)
        {
            return this.CountByState();
        }

        if (definitionIds.Count == 0)
        {
            return new Dictionary<WorkerState, int>();
        }

        var counts = new Dictionary<WorkerState, int>();
        foreach (var definitionId in definitionIds)
        {
            foreach (var state in Enum.GetValues<WorkerState>())
            {
                if (!this.byDefinitionAndState.TryGetValue((definitionId, state), out var workers) || workers.IsEmpty)
                {
                    continue;
                }

                counts[state] = counts.GetValueOrDefault(state) + workers.Count;
            }
        }

        return counts;
    }

    public int ActiveOrQueuedDefinitionCount()
    {
        var definitionIds = new HashSet<WorkDefinitionId>();
        foreach (var state in ActiveOrQueuedDefinitionStates)
        {
            foreach (var entry in this.byDefinitionAndState)
            {
                if (entry.Key.State == state && !entry.Value.IsEmpty)
                {
                    definitionIds.Add(entry.Key.DefinitionId);
                }
            }
        }

        return definitionIds.Count;
    }

    public int ActiveOrQueuedDefinitionCount(IReadOnlySet<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? this.ActiveOrQueuedDefinitionCount()
            : definitionIds.Count(definitionId =>
                ActiveOrQueuedDefinitionStates.Any(state =>
                    this.byDefinitionAndState.TryGetValue((definitionId, state), out var workers) &&
                    !workers.IsEmpty));

    public DateTimeOffset? OldestQueuedAt(IReadOnlySet<WorkDefinitionId>? definitionIds = null)
    {
        lock (this.sync)
        {
            if (definitionIds is null)
            {
                return OldestQueuedAt(this.queuedWorkers);
            }

            if (definitionIds.Count == 0)
            {
                return null;
            }

            DateTimeOffset? oldest = null;
            foreach (var definitionId in definitionIds)
            {
                if (!this.queuedWorkersByDefinition.TryGetValue(definitionId, out var queued))
                {
                    continue;
                }

                var queuedAt = OldestQueuedAt(queued);
                if (queuedAt is not null && (oldest is null || queuedAt.Value < oldest.Value))
                {
                    oldest = queuedAt;
                }
            }

            return oldest;
        }
    }

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

    public IReadOnlyList<IndexedWorkKeyTypeFacet> KeyTypes(
        WorkKeyKind? kind,
        string? type,
        string? search)
    {
        var normalizedType = string.IsNullOrWhiteSpace(type) ? null : NormalizeType(type);
        var facets = kind is { } requiredKind
            ? this.keyTypeKindCounts
                .Where(count =>
                    count.Key.Kind == requiredKind &&
                    (normalizedType is null || count.Key.Type == normalizedType) &&
                    count.Value > 0)
                .Select(count => new IndexedWorkKeyTypeFacet(
                    this.keyTypeDisplayNames.GetValueOrDefault(count.Key.Type, count.Key.Type),
                    count.Value,
                    new Dictionary<WorkKeyKind, int> { [requiredKind] = count.Value }))
            : this.keyTypeCounts
                .Where(count =>
                    (normalizedType is null || count.Key == normalizedType) &&
                    count.Value > 0)
                .Select(count => new IndexedWorkKeyTypeFacet(
                    this.keyTypeDisplayNames.GetValueOrDefault(count.Key, count.Key),
                    count.Value,
                    this.CountByKind(count.Key)));

        return [.. facets
            .Where(facet => MatchesTypeSearch(facet.Type, search))
            .OrderByDescending(facet => facet.WorkerCount)
            .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)];
    }

    public IEnumerable<WorkerId> WorkerIdsByKeyType(string type, WorkKeyKind? kind = null)
    {
        var normalizedType = NormalizeType(type);
        var references = kind is { } requiredKind
            ? this.keysByKindAndType.TryGetValue((requiredKind, normalizedType), out var typedKindReferences) ? typedKindReferences.Keys : []
            : this.keysByType.TryGetValue(normalizedType, out var typedReferences) ? typedReferences.Keys : [];
        return references
            .Select(this.ToWorkKey)
            .OfType<IndexedWorkKey>()
            .SelectMany(key => key.WorkerIds)
            .Distinct();
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
        this.RemoveState(keys.DefinitionId, keys.State, workerId);
        this.RemoveQueued(keys, workerId);
        Remove(this.byRecurrenceEnabled, keys.RecurrenceEnabled, workerId);
        Remove(this.byConcurrencyEnabled, keys.ConcurrencyEnabled, workerId);
        Remove(this.byProfilingEnabled, keys.ProfilingEnabled, workerId);

        if (keys.SubjectId is { } subjectId)
        {
            Remove(this.bySubject, subjectId, workerId);
            this.RemoveKeyReferenceIfEmpty(WorkKeyKind.Subject, subjectId.Type, subjectId.Value, this.bySubject, subjectId);
            Remove(this.byDefinitionAndSubject, (keys.DefinitionId, subjectId), workerId);
        }

        if (keys.ConcurrencyKey is { } concurrencyKey)
        {
            Remove(this.byConcurrencyKey, concurrencyKey, workerId);
            this.RemoveKeyReferenceIfEmpty(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value, this.byConcurrencyKey, concurrencyKey);
            Remove(this.byDefinitionAndConcurrencyKey, (keys.DefinitionId, concurrencyKey), workerId);
        }

        foreach (var identifier in keys.Identifiers)
        {
            Remove(this.byIdentifier, identifier, workerId);
            this.RemoveKeyReferenceIfEmpty(WorkKeyKind.Identifier, identifier.Type, identifier.Value, this.byIdentifier, identifier);
        }

        this.RemoveKeyTypeCounts(keys);
    }

    private void AddState(WorkerState state, WorkerId workerId)
        => Add(this.byState, state, workerId);

    private void AddState(WorkDefinitionId definitionId, WorkerState state, WorkerId workerId)
    {
        this.AddState(state, workerId);
        Add(this.byDefinitionAndState, (definitionId, state), workerId);
    }

    private void RemoveState(WorkerState state, WorkerId workerId)
        => Remove(this.byState, state, workerId);

    private void RemoveState(WorkDefinitionId definitionId, WorkerState state, WorkerId workerId)
    {
        this.RemoveState(state, workerId);
        Remove(this.byDefinitionAndState, (definitionId, state), workerId);
    }

    private void AddQueued(WorkerIndexKeys keys, WorkerId workerId)
    {
        if (keys.State != WorkerState.Queued)
        {
            return;
        }

        var entry = new QueuedWorkerIndexEntry(keys.StateChangedAt, workerId);
        this.queuedWorkers.Add(entry);
        if (!this.queuedWorkersByDefinition.TryGetValue(keys.DefinitionId, out var queued))
        {
            queued = [];
            this.queuedWorkersByDefinition[keys.DefinitionId] = queued;
        }

        queued.Add(entry);
    }

    private void RemoveQueued(WorkerIndexKeys keys, WorkerId workerId)
    {
        if (keys.State != WorkerState.Queued)
        {
            return;
        }

        var entry = new QueuedWorkerIndexEntry(keys.StateChangedAt, workerId);
        this.queuedWorkers.Remove(entry);
        if (!this.queuedWorkersByDefinition.TryGetValue(keys.DefinitionId, out var queued))
        {
            return;
        }

        queued.Remove(entry);
        if (queued.Count == 0)
        {
            this.queuedWorkersByDefinition.Remove(keys.DefinitionId);
        }
    }

    private static DateTimeOffset? OldestQueuedAt(SortedSet<QueuedWorkerIndexEntry> queued)
        => queued.Count == 0 ? null : queued.Min.QueuedAt;

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

        if (!HasType(existing.KeyTypes(), identifier.Type))
        {
            this.AddKeyTypeCount(existing.DefinitionId, identifier.Type);
        }

        if (!HasKindType(existing.KindTypes(), WorkKeyKind.Identifier, identifier.Type))
        {
            this.AddKeyTypeKindCount(existing.DefinitionId, WorkKeyKind.Identifier, identifier.Type);
        }
    }

    private void AddKeyTypeCounts(WorkerIndexKeys keys)
    {
        foreach (var type in keys.KeyTypes())
        {
            this.AddKeyTypeCount(type);
            this.AddKeyTypeCount(keys.DefinitionId, type);
        }

        foreach (var kindType in keys.KindTypes())
        {
            this.AddKeyTypeKindCount(kindType.Kind, kindType.Type);
            this.AddKeyTypeKindCount(keys.DefinitionId, kindType.Kind, kindType.Type);
        }
    }

    private void RemoveKeyTypeCounts(WorkerIndexKeys keys)
    {
        foreach (var type in keys.KeyTypes())
        {
            this.RemoveKeyTypeCount(type);
            this.RemoveKeyTypeCount(keys.DefinitionId, type);
        }

        foreach (var kindType in keys.KindTypes())
        {
            this.RemoveKeyTypeKindCount(kindType.Kind, kindType.Type);
            this.RemoveKeyTypeKindCount(keys.DefinitionId, kindType.Kind, kindType.Type);
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
        RemoveCount(this.keyTypeCounts, normalizedType);
        this.RemoveKeyTypeDisplayNameIfUnused(normalizedType);
    }

    private void AddKeyTypeCount(WorkDefinitionId definitionId, string type)
    {
        var normalizedType = NormalizeType(type);
        this.keyTypeDisplayNames.TryAdd(normalizedType, type);
        this.keyTypeCountsByDefinition.AddOrUpdate((definitionId, normalizedType), 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeCount(WorkDefinitionId definitionId, string type)
    {
        var normalizedType = NormalizeType(type);
        RemoveCount(this.keyTypeCountsByDefinition, (definitionId, normalizedType));
    }

    private void AddKeyTypeKindCount(WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkKeyTypeKind(kind, NormalizeType(type));
        this.keyTypeKindCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeKindCount(WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkKeyTypeKind(kind, NormalizeType(type));
        RemoveCount(this.keyTypeKindCounts, key);
    }

    private void AddKeyTypeKindCount(WorkDefinitionId definitionId, WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkDefinitionKeyTypeKind(definitionId, kind, NormalizeType(type));
        this.keyTypeKindCountsByDefinition.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeKindCount(WorkDefinitionId definitionId, WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkDefinitionKeyTypeKind(definitionId, kind, NormalizeType(type));
        RemoveCount(this.keyTypeKindCountsByDefinition, key);
    }

    private void RemoveKeyTypeDisplayNameIfUnused(string normalizedType)
    {
        if (!this.keyTypeCounts.ContainsKey(normalizedType))
        {
            this.keyTypeDisplayNames.TryRemove(normalizedType, out _);
        }
    }

    private Dictionary<WorkKeyKind, int> CountByKind(string normalizedType)
        => this.keyTypeKindCounts
            .Where(count => count.Key.Type == normalizedType && count.Value > 0)
            .ToDictionary(count => count.Key.Kind, count => count.Value);

    private Dictionary<WorkKeyKind, int> CountByKind(
        string normalizedType,
        IReadOnlySet<WorkDefinitionId> definitionIds)
        => this.keyTypeKindCountsByDefinition
            .Where(count =>
                definitionIds.Contains(count.Key.DefinitionId) &&
                count.Key.Type == normalizedType &&
                count.Value > 0)
            .GroupBy(count => count.Key.Kind)
            .ToDictionary(group => group.Key, group => group.Sum(count => count.Value));

    private CandidateSet<WorkerId> GetDefinitionCandidates(
        IReadOnlySet<WorkDefinitionId> definitionIds,
        IReadOnlySet<WorkerState>? states)
    {
        var workerIds = new HashSet<WorkerId>();
        if (states is { Count: > 0 })
        {
            foreach (var definitionId in definitionIds)
            {
                foreach (var state in states)
                {
                    if (!this.byDefinitionAndState.TryGetValue((definitionId, state), out var indexedWorkerIds))
                    {
                        continue;
                    }

                    foreach (var workerId in indexedWorkerIds.Keys)
                    {
                        workerIds.Add(workerId);
                    }
                }
            }
        }
        else
        {
            foreach (var definitionId in definitionIds)
            {
                if (!this.byDefinition.TryGetValue(definitionId, out var indexedWorkerIds))
                {
                    continue;
                }

                foreach (var workerId in indexedWorkerIds.Keys)
                {
                    workerIds.Add(workerId);
                }
            }
        }

        return new CandidateSet<WorkerId>(workerIds.Count, workerIds);
    }

    private IEnumerable<IndexedWorkKey> WorkKeysByKind(WorkKeyKind kind)
        => kind switch
        {
            WorkKeyKind.Subject => EnumerateKeys(this.bySubject, WorkKeyKind.Subject),
            WorkKeyKind.ConcurrencyKey => EnumerateKeys(this.byConcurrencyKey, WorkKeyKind.ConcurrencyKey),
            WorkKeyKind.Identifier => EnumerateKeys(this.byIdentifier, WorkKeyKind.Identifier),
            _ => [],
        };

    private bool TryGetWorkKey(
        WorkKeyKind kind,
        string type,
        string value,
        out IndexedWorkKey key)
    {
        key = default;
        switch (kind)
        {
            case WorkKeyKind.Subject:
                if (this.bySubject.TryGetValue(new WorkSubjectId(type, value), out var subjects))
                {
                    key = new IndexedWorkKey(kind, type, value, [.. subjects.Keys]);
                    return true;
                }

                return false;
            case WorkKeyKind.ConcurrencyKey:
                if (this.byConcurrencyKey.TryGetValue(new WorkConcurrencyKey(type, value), out var concurrencyKeys))
                {
                    key = new IndexedWorkKey(kind, type, value, [.. concurrencyKeys.Keys]);
                    return true;
                }

                return false;
            case WorkKeyKind.Identifier:
                if (this.byIdentifier.TryGetValue(new WorkIdentifier(type, value), out var identifiers))
                {
                    key = new IndexedWorkKey(kind, type, value, [.. identifiers.Keys]);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private IndexedWorkKey? ToWorkKey(IndexedWorkKeyReference reference)
        => this.TryGetWorkKey(reference.Kind, reference.Type, reference.Value, out var key) ? key : null;

    private void AddKeyReference(WorkKeyKind kind, string type, string value)
    {
        var reference = new IndexedWorkKeyReference(kind, type, value);
        var normalizedType = NormalizeType(type);
        Add(this.keysByType, normalizedType, reference);
        Add(this.keysByKindAndType, (kind, normalizedType), reference);
    }

    private void RemoveKeyReferenceIfEmpty<TKey>(
        WorkKeyKind kind,
        string type,
        string value,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key)
        where TKey : notnull
    {
        if (index.TryGetValue(key, out var workerIds) && !workerIds.IsEmpty)
        {
            return;
        }

        var reference = new IndexedWorkKeyReference(kind, type, value);
        var normalizedType = NormalizeType(type);
        Remove(this.keysByType, normalizedType, reference);
        Remove(this.keysByKindAndType, (kind, normalizedType), reference);
    }

    private static void Add<TKey, TValue>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<TValue, byte>> index,
        TKey key,
        TValue value)
        where TKey : notnull
        where TValue : notnull
    {
        var values = index.GetOrAdd(key, static _ => []);
        values[value] = 0;
    }

    private static bool Remove<TKey, TValue>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<TValue, byte>> index,
        TKey key,
        TValue value)
        where TKey : notnull
        where TValue : notnull
    {
        if (!index.TryGetValue(key, out var values))
        {
            return false;
        }

        var removed = values.TryRemove(value, out _);
        if (values.IsEmpty)
        {
            index.TryRemove(new KeyValuePair<TKey, ConcurrentDictionary<TValue, byte>>(key, values));
        }

        return removed;
    }

    private static void RemoveCount<TKey>(ConcurrentDictionary<TKey, int> counts, TKey key)
        where TKey : notnull
    {
        while (counts.TryGetValue(key, out var current))
        {
            var next = current - 1;
            if (next <= 0)
            {
                if (counts.TryRemove(new KeyValuePair<TKey, int>(key, current)))
                {
                    return;
                }

                continue;
            }

            if (counts.TryUpdate(key, next, current))
            {
                return;
            }
        }
    }

    private static bool TryAddCandidate<TKey>(
        List<CandidateSet<WorkerId>> candidates,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey key)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var workerIds) || workerIds.IsEmpty)
        {
            return false;
        }

        candidates.Add(new CandidateSet<WorkerId>(workerIds.Count, workerIds.Keys));
        return true;
    }

    private static bool AddIfPresent<TKey>(
        List<CandidateSet<WorkerId>> candidates,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerId, byte>> index,
        TKey? key)
        where TKey : struct
    {
        if (key is null)
        {
            return true;
        }

        return TryAddCandidate(candidates, index, key.Value);
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

    private readonly record struct CandidateSet<T>(
        int Count,
        IEnumerable<T> Values);

    private readonly record struct IndexedWorkKeyTypeKind(
        WorkKeyKind Kind,
        string Type);

    private readonly record struct IndexedWorkKeyReference(
        WorkKeyKind Kind,
        string Type,
        string Value);

    private readonly record struct IndexedWorkDefinitionKeyTypeKind(
        WorkDefinitionId DefinitionId,
        WorkKeyKind Kind,
        string Type);

    private readonly record struct QueuedWorkerIndexEntry(
        DateTimeOffset QueuedAt,
        WorkerId WorkerId) : IComparable<QueuedWorkerIndexEntry>
    {
        public int CompareTo(QueuedWorkerIndexEntry other)
        {
            var queuedAt = this.QueuedAt.CompareTo(other.QueuedAt);
            return queuedAt != 0 ? queuedAt : this.WorkerId.Value.CompareTo(other.WorkerId.Value);
        }
    }

    private sealed record WorkerIndexKeys(
        WorkDefinitionId DefinitionId,
        WorkerState State,
        DateTimeOffset StateChangedAt,
        bool RecurrenceEnabled,
        bool ConcurrencyEnabled,
        bool ProfilingEnabled,
        WorkSubjectId? SubjectId,
        WorkConcurrencyKey? ConcurrencyKey,
        HashSet<WorkIdentifier> Identifiers)
    {
        public static WorkerIndexKeys From(WorkerRecord worker)
            => new(
                worker.Work.Definition.Id,
                worker.State,
                worker.StateChangedAt,
                worker.Configuration.Recurrence.IsEnabled,
                worker.Configuration.Concurrency.IsEnabled,
                worker.Options.ProfilingEnabled,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers.ToHashSet());

        public HashSet<string> KeyTypes()
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

        public HashSet<(WorkKeyKind Kind, string Type)> KindTypes()
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

    private static string NormalizeType(string type)
        => type.ToUpperInvariant();

    private static bool MatchesTypeSearch(string type, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var terms = search.Split(
                [' ', '\t', '\r', '\n', '.', ',', ':', ';', '-', '_', '/', '\\', '#', '=', '&', '?'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => !IsIgnoredSearchTerm(term))
            .ToArray();
        return terms.Length == 0 || terms.All(term => type.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIgnoredSearchTerm(string term)
        => term.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("for", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("keys", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("the", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("work", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("worker", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("workers", StringComparison.OrdinalIgnoreCase);
}
