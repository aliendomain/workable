using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkerIterationIndex
{
    private readonly Lock sync = new();
    private readonly int recentIterationLimit;
    private readonly ConcurrentDictionary<WorkerIterationReference, IndexedWorkerIteration> iterations = [];
    private readonly ConcurrentDictionary<WorkerId, ConcurrentDictionary<WorkerIterationReference, byte>> byWorker = [];
    private readonly ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<WorkerIterationReference, byte>> byDefinition = [];
    private readonly ConcurrentDictionary<WorkCompletionStatus, ConcurrentDictionary<WorkerIterationReference, byte>> byStatus = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, WorkCompletionStatus Status), ConcurrentDictionary<WorkerIterationReference, byte>> byDefinitionAndStatus = [];
    private readonly ConcurrentDictionary<WorkSubjectId, ConcurrentDictionary<WorkerIterationReference, byte>> bySubject = [];
    private readonly ConcurrentDictionary<WorkConcurrencyKey, ConcurrentDictionary<WorkerIterationReference, byte>> byConcurrencyKey = [];
    private readonly ConcurrentDictionary<WorkIdentifier, ConcurrentDictionary<WorkerIterationReference, byte>> byIdentifier = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IndexedWorkIterationKeyReference, byte>> keysByType = [];
    private readonly ConcurrentDictionary<(WorkKeyKind Kind, string Type), ConcurrentDictionary<IndexedWorkIterationKeyReference, byte>> keysByKindAndType = [];
    private readonly Dictionary<WorkCompletionStatus, SortedSet<RecentIterationReference>> recentByStatus = [];
    private readonly Dictionary<(WorkDefinitionId DefinitionId, WorkCompletionStatus Status), SortedSet<RecentIterationReference>> recentByDefinitionAndStatus = [];
    private readonly ConcurrentDictionary<string, int> keyTypeCounts = [];
    private readonly ConcurrentDictionary<(WorkDefinitionId DefinitionId, string Type), int> keyTypeCountsByDefinition = [];
    private readonly ConcurrentDictionary<IndexedWorkIterationKeyTypeKind, int> keyTypeKindCounts = [];
    private readonly ConcurrentDictionary<IndexedWorkDefinitionIterationKeyTypeKind, int> keyTypeKindCountsByDefinition = [];
    private readonly ConcurrentDictionary<string, string> keyTypeDisplayNames = [];

    public WorkerIterationIndex(int recentIterationLimit)
    {
        this.recentIterationLimit = Math.Max(0, recentIterationLimit);
    }

    public void Register(WorkerRecord worker, WorkerIterationSnapshot iteration)
    {
        var indexed = IndexedWorkerIteration.From(worker, iteration);
        lock (this.sync)
        {
            if (this.iterations.TryGetValue(indexed.Reference, out var existing))
            {
                this.RemoveIndexes(existing);
            }

            this.iterations[indexed.Reference] = indexed;
            this.AddIndexes(indexed);
        }
    }

    public WorkerIterationSnapshot? Get(WorkerIterationReference reference)
        => this.iterations.TryGetValue(reference, out var iteration) ? iteration.Snapshot : null;

    public IEnumerable<IndexedWorkerIteration> Find(WorkerIterationCriteria query)
        => this.Find(query, null);

    public IEnumerable<IndexedWorkerIteration> Find(
        WorkerIterationCriteria query,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var candidateReferences = this.FindBestCandidates(query, definitionIds);
        return candidateReferences is null
            ? this.iterations.Values
            : candidateReferences
                .Select(reference => this.iterations.TryGetValue(reference, out var iteration) ? iteration : null)
                .OfType<IndexedWorkerIteration>();
    }

    public IEnumerable<IndexedWorkIterationKey> WorkKeys()
        => EnumerateKeys(this.bySubject, WorkKeyKind.Subject)
            .Concat(EnumerateKeys(this.byConcurrencyKey, WorkKeyKind.ConcurrencyKey))
            .Concat(EnumerateKeys(this.byIdentifier, WorkKeyKind.Identifier));

    public IEnumerable<IndexedWorkIterationKey> WorkKeys(WorkKeyKind? kind, string? type, string? value)
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
                    .OfType<IndexedWorkIterationKey>()
                : [];
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            var normalizedType = NormalizeType(type);
            var references = kind is { } typeKind
                ? this.keysByKindAndType.TryGetValue((typeKind, normalizedType), out var typedKindReferences) ? typedKindReferences.Keys : []
                : this.keysByType.TryGetValue(normalizedType, out var typedReferences) ? typedReferences.Keys : [];
            return references.Select(this.ToWorkKey).OfType<IndexedWorkIterationKey>();
        }

        return kind is null
            ? this.WorkKeys()
            : this.WorkKeysByKind(kind.Value);
    }

    public IReadOnlyList<WorkerIterationOverviewItem> GetOverviewItems(
        IEnumerable<WorkerIterationReference> references,
        IReadOnlySet<WorkCompletionStatus>? statuses = null)
        => [.. references
            .Select(reference => this.iterations.TryGetValue(reference, out var iteration) ? iteration : null)
            .OfType<IndexedWorkerIteration>()
            .Where(iteration => statuses is null || statuses.Contains(iteration.Status))
            .OrderByDescending(iteration => iteration.CompletedAt)
            .Select(iteration => iteration.ToOverviewItem())];

    public void Forget(WorkerRecord worker)
    {
        lock (this.sync)
        {
            if (!this.byWorker.TryRemove(worker.Id, out var references))
            {
                return;
            }

            foreach (var reference in references.Keys)
            {
                this.ForgetLocked(reference);
            }
        }
    }

    public void Forget(WorkerIterationReference reference)
    {
        lock (this.sync)
        {
            this.ForgetLocked(reference);
        }
    }

    public void Clear()
    {
        lock (this.sync)
        {
            this.iterations.Clear();
            this.byWorker.Clear();
            this.byDefinition.Clear();
            this.byStatus.Clear();
            this.byDefinitionAndStatus.Clear();
            this.bySubject.Clear();
            this.byConcurrencyKey.Clear();
            this.byIdentifier.Clear();
            this.keysByType.Clear();
            this.keysByKindAndType.Clear();
            this.recentByStatus.Clear();
            this.recentByDefinitionAndStatus.Clear();
            this.keyTypeCounts.Clear();
            this.keyTypeCountsByDefinition.Clear();
            this.keyTypeKindCounts.Clear();
            this.keyTypeKindCountsByDefinition.Clear();
            this.keyTypeDisplayNames.Clear();
        }
    }

    public IReadOnlyDictionary<WorkCompletionStatus, int> CountByStatus()
        => this.byStatus
            .Where(count => !count.Value.IsEmpty)
            .ToDictionary(count => count.Key, count => count.Value.Count);

    public IReadOnlyDictionary<WorkCompletionStatus, int> CountByStatus(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        if (definitionIds is null)
        {
            return this.CountByStatus();
        }

        if (definitionIds.Count == 0)
        {
            return new Dictionary<WorkCompletionStatus, int>();
        }

        var counts = new Dictionary<WorkCompletionStatus, int>();
        foreach (var definitionId in definitionIds)
        {
            foreach (var status in Enum.GetValues<WorkCompletionStatus>())
            {
                if (!this.byDefinitionAndStatus.TryGetValue((definitionId, status), out var iterations) || iterations.IsEmpty)
                {
                    continue;
                }

                counts[status] = counts.GetValueOrDefault(status) + iterations.Count;
            }
        }

        return counts;
    }

    public IReadOnlyList<IndexedWorkIterationKeyTypeFacet> CommonKeyTypes(
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
                .Select(group => new IndexedWorkIterationKeyTypeFacet(
                    this.keyTypeDisplayNames.GetValueOrDefault(group.Key, group.Key),
                    group.Sum(count => count.Value),
                    this.CountByKind(group.Key, definitionIds)))
                .OrderByDescending(facet => facet.IterationCount)
                .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)
                .Take(normalizedTake)];
        }

        return [.. this.keyTypeCounts
            .Where(count => count.Value > 0)
            .Select(count => new IndexedWorkIterationKeyTypeFacet(
                this.keyTypeDisplayNames.GetValueOrDefault(count.Key, count.Key),
                count.Value,
                this.CountByKind(count.Key)))
            .OrderByDescending(facet => facet.IterationCount)
            .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedTake)];
    }

    public IReadOnlyList<WorkerIterationOverviewItem> RecentByStatus(WorkCompletionStatus status, int take)
    {
        var normalizedTake = Math.Max(0, take);
        if (normalizedTake == 0)
        {
            return [];
        }

        lock (this.sync)
        {
            return this.recentByStatus.TryGetValue(status, out var references)
                ? [.. references
                    .Take(normalizedTake)
                    .Select(reference => this.iterations.TryGetValue(reference.Reference, out var iteration) ? iteration : null)
                    .OfType<IndexedWorkerIteration>()
                    .Select(iteration => iteration.ToOverviewItem())]
                : [];
        }
    }

    public IReadOnlyList<WorkerIterationOverviewItem> RecentByStatus(
        WorkCompletionStatus status,
        int take,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        if (definitionIds is null)
        {
            return this.RecentByStatus(status, take);
        }

        if (definitionIds.Count == 0)
        {
            return [];
        }

        var normalizedTake = Math.Max(0, take);
        if (normalizedTake == 0)
        {
            return [];
        }

        var recent = new List<RecentIterationReference>();
        lock (this.sync)
        {
            foreach (var definitionId in definitionIds)
            {
                if (!this.recentByDefinitionAndStatus.TryGetValue((definitionId, status), out var references))
                {
                    continue;
                }

                recent.AddRange(references.Take(normalizedTake));
            }
        }

        return [.. recent
            .Order()
            .Take(normalizedTake)
            .Select(reference => this.iterations.TryGetValue(reference.Reference, out var iteration) ? iteration : null)
            .OfType<IndexedWorkerIteration>()
            .Select(iteration => iteration.ToOverviewItem())];
    }

    private HashSet<WorkerIterationReference>? FindBestCandidates(
        WorkerIterationCriteria query,
        IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        if (definitionIds is { Count: 0 })
        {
            return new HashSet<WorkerIterationReference>();
        }

        var candidates = new List<CandidateSet<WorkerIterationReference>>();

        if (definitionIds is { Count: > 0 })
        {
            var definitionCandidates = this.GetDefinitionCandidates(definitionIds, query.Statuses);
            if (definitionCandidates.Count == 0)
            {
                return new HashSet<WorkerIterationReference>();
            }

            candidates.Add(definitionCandidates);
        }

        if (!AddIfPresent(candidates, this.byWorker, query.WorkerId) ||
            !AddIfPresent(candidates, this.byDefinition, query.DefinitionId) ||
            !AddIfPresent(candidates, this.bySubject, query.SubjectId) ||
            !AddIfPresent(candidates, this.byConcurrencyKey, query.ConcurrencyKey) ||
            !AddIfPresent(candidates, this.byIdentifier, query.Identifier))
        {
            return new HashSet<WorkerIterationReference>();
        }

        if (query.Statuses is { Count: > 0 } statuses)
        {
            var references = new HashSet<WorkerIterationReference>();
            foreach (var status in statuses)
            {
                if (!this.byStatus.TryGetValue(status, out var statusReferences))
                {
                    continue;
                }

                foreach (var reference in statusReferences.Keys)
                {
                    references.Add(reference);
                }
            }

            if (references.Count == 0)
            {
                return new HashSet<WorkerIterationReference>();
            }

            candidates.Add(new CandidateSet<WorkerIterationReference>(references.Count, references));
        }

        return candidates.Count == 0
            ? null
            : candidates.MinBy(candidate => candidate.Count).Values.ToHashSet();
    }

    public IReadOnlyList<IndexedWorkIterationKeyTypeFacet> KeyTypes(
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
                .Select(count => new IndexedWorkIterationKeyTypeFacet(
                    this.keyTypeDisplayNames.GetValueOrDefault(count.Key.Type, count.Key.Type),
                    count.Value,
                    new Dictionary<WorkKeyKind, int> { [requiredKind] = count.Value }))
            : this.keyTypeCounts
                .Where(count =>
                    (normalizedType is null || count.Key == normalizedType) &&
                    count.Value > 0)
                .Select(count => new IndexedWorkIterationKeyTypeFacet(
                    this.keyTypeDisplayNames.GetValueOrDefault(count.Key, count.Key),
                    count.Value,
                    this.CountByKind(count.Key)));

        return [.. facets
            .Where(facet => MatchesTypeSearch(facet.Type, search))
            .OrderByDescending(facet => facet.IterationCount)
            .ThenBy(facet => facet.Type, StringComparer.OrdinalIgnoreCase)];
    }

    public IEnumerable<WorkerIterationReference> IterationReferencesByKeyType(string type, WorkKeyKind? kind = null)
    {
        var normalizedType = NormalizeType(type);
        var references = kind is { } requiredKind
            ? this.keysByKindAndType.TryGetValue((requiredKind, normalizedType), out var typedKindReferences) ? typedKindReferences.Keys : []
            : this.keysByType.TryGetValue(normalizedType, out var typedReferences) ? typedReferences.Keys : [];
        return references
            .Select(this.ToWorkKey)
            .OfType<IndexedWorkIterationKey>()
            .SelectMany(key => key.IterationReferences)
            .Distinct();
    }

    private CandidateSet<WorkerIterationReference> GetDefinitionCandidates(
        IReadOnlySet<WorkDefinitionId> definitionIds,
        IReadOnlySet<WorkCompletionStatus>? statuses)
    {
        var references = new HashSet<WorkerIterationReference>();
        if (statuses is { Count: > 0 })
        {
            foreach (var definitionId in definitionIds)
            {
                foreach (var status in statuses)
                {
                    if (!this.byDefinitionAndStatus.TryGetValue((definitionId, status), out var indexedReferences))
                    {
                        continue;
                    }

                    foreach (var reference in indexedReferences.Keys)
                    {
                        references.Add(reference);
                    }
                }
            }
        }
        else
        {
            foreach (var definitionId in definitionIds)
            {
                if (!this.byDefinition.TryGetValue(definitionId, out var indexedReferences))
                {
                    continue;
                }

                foreach (var reference in indexedReferences.Keys)
                {
                    references.Add(reference);
                }
            }
        }

        return new CandidateSet<WorkerIterationReference>(references.Count, references);
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

    private void ForgetLocked(WorkerIterationReference reference)
    {
        if (this.iterations.TryRemove(reference, out var iteration))
        {
            this.RemoveIndexes(iteration);
        }
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
        List<CandidateSet<WorkerIterationReference>> candidates,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        TKey key)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var references) || references.IsEmpty)
        {
            return false;
        }

        candidates.Add(new CandidateSet<WorkerIterationReference>(references.Count, references.Keys));
        return true;
    }

    private static bool AddIfPresent<TKey>(
        List<CandidateSet<WorkerIterationReference>> candidates,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        TKey? key)
        where TKey : struct
    {
        if (key is null)
        {
            return true;
        }

        return TryAddCandidate(candidates, index, key.Value);
    }

    private static IEnumerable<IndexedWorkIterationKey> EnumerateKeys<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        WorkKeyKind kind)
        where TKey : struct, IWorkKey
        => index.Select(entry => new IndexedWorkIterationKey(kind, entry.Key.Type, entry.Key.Value, [.. entry.Value.Keys]));

    private IEnumerable<IndexedWorkIterationKey> WorkKeysByKind(WorkKeyKind kind)
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
        out IndexedWorkIterationKey key)
    {
        key = default;
        switch (kind)
        {
            case WorkKeyKind.Subject:
                if (this.bySubject.TryGetValue(new WorkSubjectId(type, value), out var subjects))
                {
                    key = new IndexedWorkIterationKey(kind, type, value, [.. subjects.Keys]);
                    return true;
                }

                return false;
            case WorkKeyKind.ConcurrencyKey:
                if (this.byConcurrencyKey.TryGetValue(new WorkConcurrencyKey(type, value), out var concurrencyKeys))
                {
                    key = new IndexedWorkIterationKey(kind, type, value, [.. concurrencyKeys.Keys]);
                    return true;
                }

                return false;
            case WorkKeyKind.Identifier:
                if (this.byIdentifier.TryGetValue(new WorkIdentifier(type, value), out var identifiers))
                {
                    key = new IndexedWorkIterationKey(kind, type, value, [.. identifiers.Keys]);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private IndexedWorkIterationKey? ToWorkKey(IndexedWorkIterationKeyReference reference)
        => this.TryGetWorkKey(reference.Kind, reference.Type, reference.Value, out var key) ? key : null;

    private void AddKeyReference(WorkKeyKind kind, string type, string value)
    {
        var reference = new IndexedWorkIterationKeyReference(kind, type, value);
        var normalizedType = NormalizeType(type);
        Add(this.keysByType, normalizedType, reference);
        Add(this.keysByKindAndType, (kind, normalizedType), reference);
    }

    private void RemoveKeyReferenceIfEmpty<TKey>(
        WorkKeyKind kind,
        string type,
        string value,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        TKey key)
        where TKey : notnull
    {
        if (index.TryGetValue(key, out var references) && !references.IsEmpty)
        {
            return;
        }

        var reference = new IndexedWorkIterationKeyReference(kind, type, value);
        var normalizedType = NormalizeType(type);
        Remove(this.keysByType, normalizedType, reference);
        Remove(this.keysByKindAndType, (kind, normalizedType), reference);
    }

    private void AddRecentReference(IndexedWorkerIteration iteration)
    {
        var reference = RecentIterationReference.From(iteration);
        AddRecentReference(this.recentByStatus, iteration.Status, reference);
        AddRecentReference(this.recentByDefinitionAndStatus, (iteration.DefinitionId, iteration.Status), reference);
    }

    private void RemoveRecentReference(IndexedWorkerIteration iteration)
    {
        var reference = RecentIterationReference.From(iteration);
        RemoveRecentReference(this.recentByStatus, iteration.Status, reference);
        RemoveRecentReference(this.recentByDefinitionAndStatus, (iteration.DefinitionId, iteration.Status), reference);
    }

    private void AddRecentReference<TKey>(
        Dictionary<TKey, SortedSet<RecentIterationReference>> index,
        TKey key,
        RecentIterationReference reference)
        where TKey : notnull
    {
        if (this.recentIterationLimit == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var references))
        {
            references = [];
            index[key] = references;
        }

        references.Add(reference);
        while (references.Count > this.recentIterationLimit)
        {
            references.Remove(references.Max);
        }
    }

    private static void RemoveRecentReference<TKey>(
        Dictionary<TKey, SortedSet<RecentIterationReference>> index,
        TKey key,
        RecentIterationReference reference)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var references))
        {
            return;
        }

        references.Remove(reference);
        if (references.Count == 0)
        {
            index.Remove(key);
        }
    }

    private void AddIndexes(IndexedWorkerIteration iteration)
    {
        Add(this.byWorker, iteration.WorkerId, iteration.Reference);
        Add(this.byDefinition, iteration.DefinitionId, iteration.Reference);
        Add(this.byStatus, iteration.Status, iteration.Reference);
        Add(this.byDefinitionAndStatus, (iteration.DefinitionId, iteration.Status), iteration.Reference);
        this.AddRecentReference(iteration);
        if (iteration.SubjectId is { } subjectId)
        {
            Add(this.bySubject, subjectId, iteration.Reference);
            this.AddKeyReference(WorkKeyKind.Subject, subjectId.Type, subjectId.Value);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            Add(this.byConcurrencyKey, concurrencyKey, iteration.Reference);
            this.AddKeyReference(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            Add(this.byIdentifier, identifier, iteration.Reference);
            this.AddKeyReference(WorkKeyKind.Identifier, identifier.Type, identifier.Value);
        }

        this.AddKeyTypeCounts(iteration);
    }

    private void RemoveIndexes(IndexedWorkerIteration iteration)
    {
        Remove(this.byWorker, iteration.WorkerId, iteration.Reference);
        Remove(this.byDefinition, iteration.DefinitionId, iteration.Reference);
        Remove(this.byStatus, iteration.Status, iteration.Reference);
        Remove(this.byDefinitionAndStatus, (iteration.DefinitionId, iteration.Status), iteration.Reference);
        this.RemoveRecentReference(iteration);
        if (iteration.SubjectId is { } subjectId)
        {
            Remove(this.bySubject, subjectId, iteration.Reference);
            this.RemoveKeyReferenceIfEmpty(WorkKeyKind.Subject, subjectId.Type, subjectId.Value, this.bySubject, subjectId);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            Remove(this.byConcurrencyKey, concurrencyKey, iteration.Reference);
            this.RemoveKeyReferenceIfEmpty(WorkKeyKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value, this.byConcurrencyKey, concurrencyKey);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            Remove(this.byIdentifier, identifier, iteration.Reference);
            this.RemoveKeyReferenceIfEmpty(WorkKeyKind.Identifier, identifier.Type, identifier.Value, this.byIdentifier, identifier);
        }

        this.RemoveKeyTypeCounts(iteration);
    }

    private void AddKeyTypeCounts(IndexedWorkerIteration iteration)
    {
        foreach (var type in iteration.KeyTypes())
        {
            this.AddKeyTypeCount(type);
            this.AddKeyTypeCount(iteration.DefinitionId, type);
        }

        foreach (var kindType in iteration.KindTypes())
        {
            this.AddKeyTypeKindCount(kindType.Kind, kindType.Type);
            this.AddKeyTypeKindCount(iteration.DefinitionId, kindType.Kind, kindType.Type);
        }
    }

    private void RemoveKeyTypeCounts(IndexedWorkerIteration iteration)
    {
        foreach (var type in iteration.KeyTypes())
        {
            this.RemoveKeyTypeCount(type);
            this.RemoveKeyTypeCount(iteration.DefinitionId, type);
        }

        foreach (var kindType in iteration.KindTypes())
        {
            this.RemoveKeyTypeKindCount(kindType.Kind, kindType.Type);
            this.RemoveKeyTypeKindCount(iteration.DefinitionId, kindType.Kind, kindType.Type);
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
        var key = new IndexedWorkIterationKeyTypeKind(kind, NormalizeType(type));
        this.keyTypeKindCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeKindCount(WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkIterationKeyTypeKind(kind, NormalizeType(type));
        RemoveCount(this.keyTypeKindCounts, key);
    }

    private void AddKeyTypeKindCount(WorkDefinitionId definitionId, WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkDefinitionIterationKeyTypeKind(definitionId, kind, NormalizeType(type));
        this.keyTypeKindCountsByDefinition.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeKindCount(WorkDefinitionId definitionId, WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkDefinitionIterationKeyTypeKind(definitionId, kind, NormalizeType(type));
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

    public sealed record IndexedWorkerIteration(
        WorkerIterationReference Reference,
        WorkerId WorkerId,
        long Sequence,
        WorkDefinitionId DefinitionId,
        string DefinitionName,
        string Category,
        WorkerState WorkerState,
        WorkCompletionStatus Status,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        TimeSpan ExecutionDuration,
        WorkSubjectId? SubjectId,
        WorkConcurrencyKey? ConcurrencyKey,
        IReadOnlySet<WorkIdentifier> Identifiers,
        WorkerIterationSnapshot Snapshot)
    {
        public static IndexedWorkerIteration From(WorkerRecord worker, WorkerIterationSnapshot iteration)
            => new(
                new WorkerIterationReference(worker.Id, iteration.Sequence),
                worker.Id,
                iteration.Sequence,
                worker.Work.Definition.Id,
                worker.Work.Definition.Name,
                worker.Work.Definition.Category,
                worker.State,
                iteration.Status,
                iteration.StartedAt,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                worker.SubjectId,
                worker.ConcurrencyKey,
                worker.Identifiers,
                iteration);

        public WorkerIterationOverviewItem ToOverviewItem()
            => new(
                this.WorkerId,
                this.Sequence,
                this.DefinitionId,
                this.DefinitionName,
                this.Category,
                this.WorkerState,
                this.Status,
                this.StartedAt,
                this.CompletedAt,
                this.ExecutionDuration,
                this.SubjectId,
                this.ConcurrencyKey,
                [.. this.Identifiers]);

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

    public readonly record struct IndexedWorkIterationKeyTypeFacet(
        string Type,
        int IterationCount,
        IReadOnlyDictionary<WorkKeyKind, int> IterationCountByKind);

    public readonly record struct IndexedWorkIterationKey(
        WorkKeyKind Kind,
        string Type,
        string Value,
        IReadOnlyList<WorkerIterationReference> IterationReferences);

    private readonly record struct CandidateSet<T>(
        int Count,
        IEnumerable<T> Values);

    private readonly record struct IndexedWorkIterationKeyReference(
        WorkKeyKind Kind,
        string Type,
        string Value);

    private readonly record struct IndexedWorkIterationKeyTypeKind(
        WorkKeyKind Kind,
        string Type);

    private readonly record struct IndexedWorkDefinitionIterationKeyTypeKind(
        WorkDefinitionId DefinitionId,
        WorkKeyKind Kind,
        string Type);

    private sealed class KindTypeComparer : IEqualityComparer<(WorkKeyKind Kind, string Type)>
    {
        public static KindTypeComparer Instance { get; } = new();

        public bool Equals((WorkKeyKind Kind, string Type) x, (WorkKeyKind Kind, string Type) y)
            => x.Kind == y.Kind && string.Equals(x.Type, y.Type, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((WorkKeyKind Kind, string Type) obj)
            => HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Type));
    }

    private static string NormalizeType(string type)
        => type.ToUpperInvariant();

    private readonly record struct RecentIterationReference(
        DateTimeOffset CompletedAt,
        WorkerId WorkerId,
        long Sequence,
        WorkerIterationReference Reference) : IComparable<RecentIterationReference>
    {
        public static RecentIterationReference From(IndexedWorkerIteration iteration)
            => new(
                iteration.CompletedAt,
                iteration.WorkerId,
                iteration.Sequence,
                iteration.Reference);

        public int CompareTo(RecentIterationReference other)
        {
            var completedAt = other.CompletedAt.CompareTo(this.CompletedAt);
            if (completedAt != 0)
            {
                return completedAt;
            }

            var sequence = other.Sequence.CompareTo(this.Sequence);
            if (sequence != 0)
            {
                return sequence;
            }

            return this.WorkerId.Value.CompareTo(other.WorkerId.Value);
        }
    }

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
