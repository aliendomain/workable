using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkerIterationIndex
{
    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkerIterationReference, IndexedWorkerIteration> iterations = [];
    private readonly ConcurrentDictionary<WorkerId, ConcurrentDictionary<WorkerIterationReference, byte>> byWorker = [];
    private readonly ConcurrentDictionary<WorkDefinitionId, ConcurrentDictionary<WorkerIterationReference, byte>> byDefinition = [];
    private readonly ConcurrentDictionary<WorkCompletionStatus, ConcurrentDictionary<WorkerIterationReference, byte>> byStatus = [];
    private readonly ConcurrentDictionary<WorkSubjectId, ConcurrentDictionary<WorkerIterationReference, byte>> bySubject = [];
    private readonly ConcurrentDictionary<WorkConcurrencyKey, ConcurrentDictionary<WorkerIterationReference, byte>> byConcurrencyKey = [];
    private readonly ConcurrentDictionary<WorkIdentifier, ConcurrentDictionary<WorkerIterationReference, byte>> byIdentifier = [];
    private readonly ConcurrentDictionary<string, int> keyTypeCounts = [];
    private readonly ConcurrentDictionary<IndexedWorkIterationKeyTypeKind, int> keyTypeKindCounts = [];
    private readonly ConcurrentDictionary<string, string> keyTypeDisplayNames = [];

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

    public IEnumerable<IndexedWorkerIteration> Find(WorkerIterationQuery query)
    {
        var candidateReferences = this.FindBestCandidates(query);
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
                if (!this.iterations.TryRemove(reference, out var iteration))
                {
                    continue;
                }

                this.RemoveIndexes(iteration);
            }
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
            this.bySubject.Clear();
            this.byConcurrencyKey.Clear();
            this.byIdentifier.Clear();
            this.keyTypeCounts.Clear();
            this.keyTypeKindCounts.Clear();
            this.keyTypeDisplayNames.Clear();
        }
    }

    public IReadOnlyDictionary<WorkCompletionStatus, int> CountByStatus()
        => this.byStatus
            .Where(count => !count.Value.IsEmpty)
            .ToDictionary(count => count.Key, count => count.Value.Count);

    public IReadOnlyList<IndexedWorkIterationKeyTypeFacet> CommonKeyTypes(int take)
    {
        var normalizedTake = Math.Max(0, take);
        if (normalizedTake == 0)
        {
            return [];
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
        => this.byStatus.TryGetValue(status, out var references)
            ? [.. references.Keys
                .Select(reference => this.iterations.TryGetValue(reference, out var iteration) ? iteration : null)
                .OfType<IndexedWorkerIteration>()
                .OrderByDescending(iteration => iteration.CompletedAt)
                .Take(Math.Max(0, take))
                .Select(iteration => iteration.ToOverviewItem())]
            : [];

    private IReadOnlySet<WorkerIterationReference>? FindBestCandidates(WorkerIterationQuery query)
    {
        var candidates = new List<IReadOnlyCollection<WorkerIterationReference>>();

        AddIfPresent(candidates, this.byWorker, query.WorkerId);
        AddIfPresent(candidates, this.byDefinition, query.DefinitionId);
        AddIfPresent(candidates, this.bySubject, query.SubjectId);
        AddIfPresent(candidates, this.byConcurrencyKey, query.ConcurrencyKey);
        AddIfPresent(candidates, this.byIdentifier, query.Identifier);

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

            candidates.Add(references);
        }

        return candidates.Count == 0
            ? null
            : candidates.MinBy(candidate => candidate.Count)?.ToHashSet();
    }

    private static void Add<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        TKey key,
        WorkerIterationReference reference)
        where TKey : notnull
    {
        var references = index.GetOrAdd(key, static _ => []);
        references[reference] = 0;
    }

    private static bool Remove<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        TKey key,
        WorkerIterationReference reference)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var references))
        {
            return false;
        }

        var removed = references.TryRemove(reference, out _);
        if (references.IsEmpty)
        {
            index.TryRemove(new KeyValuePair<TKey, ConcurrentDictionary<WorkerIterationReference, byte>>(key, references));
        }

        return removed;
    }

    private static void AddIfPresent<TKey>(
        List<IReadOnlyCollection<WorkerIterationReference>> candidates,
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        TKey? key)
        where TKey : struct
    {
        if (key is { } requiredKey && index.TryGetValue(requiredKey, out var references))
        {
            candidates.Add([.. references.Keys]);
        }
    }

    private static IEnumerable<IndexedWorkIterationKey> EnumerateKeys<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<WorkerIterationReference, byte>> index,
        WorkKeyKind kind)
        where TKey : struct, IWorkKey
        => index.Select(entry => new IndexedWorkIterationKey(kind, entry.Key.Type, entry.Key.Value, [.. entry.Value.Keys]));

    private void AddIndexes(IndexedWorkerIteration iteration)
    {
        Add(this.byWorker, iteration.WorkerId, iteration.Reference);
        Add(this.byDefinition, iteration.DefinitionId, iteration.Reference);
        Add(this.byStatus, iteration.Status, iteration.Reference);
        if (iteration.SubjectId is { } subjectId)
        {
            Add(this.bySubject, subjectId, iteration.Reference);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            Add(this.byConcurrencyKey, concurrencyKey, iteration.Reference);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            Add(this.byIdentifier, identifier, iteration.Reference);
        }

        this.AddKeyTypeCounts(iteration);
    }

    private void RemoveIndexes(IndexedWorkerIteration iteration)
    {
        Remove(this.byWorker, iteration.WorkerId, iteration.Reference);
        Remove(this.byDefinition, iteration.DefinitionId, iteration.Reference);
        Remove(this.byStatus, iteration.Status, iteration.Reference);
        if (iteration.SubjectId is { } subjectId)
        {
            Remove(this.bySubject, subjectId, iteration.Reference);
        }

        if (iteration.ConcurrencyKey is { } concurrencyKey)
        {
            Remove(this.byConcurrencyKey, concurrencyKey, iteration.Reference);
        }

        foreach (var identifier in iteration.Identifiers)
        {
            Remove(this.byIdentifier, identifier, iteration.Reference);
        }

        this.RemoveKeyTypeCounts(iteration);
    }

    private void AddKeyTypeCounts(IndexedWorkerIteration iteration)
    {
        foreach (var type in iteration.KeyTypes())
        {
            this.AddKeyTypeCount(type);
        }

        foreach (var kindType in iteration.KindTypes())
        {
            this.AddKeyTypeKindCount(kindType.Kind, kindType.Type);
        }
    }

    private void RemoveKeyTypeCounts(IndexedWorkerIteration iteration)
    {
        foreach (var type in iteration.KeyTypes())
        {
            this.RemoveKeyTypeCount(type);
        }

        foreach (var kindType in iteration.KindTypes())
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
        var key = new IndexedWorkIterationKeyTypeKind(kind, NormalizeType(type));
        this.keyTypeKindCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    private void RemoveKeyTypeKindCount(WorkKeyKind kind, string type)
    {
        var key = new IndexedWorkIterationKeyTypeKind(kind, NormalizeType(type));
        this.keyTypeKindCounts.AddOrUpdate(key, 0, static (_, count) => Math.Max(0, count - 1));
    }

    private IReadOnlyDictionary<WorkKeyKind, int> CountByKind(string normalizedType)
        => this.keyTypeKindCounts
            .Where(count => count.Key.Type == normalizedType && count.Value > 0)
            .ToDictionary(count => count.Key.Kind, count => count.Value);

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

    private readonly record struct IndexedWorkIterationKeyTypeKind(
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
}
