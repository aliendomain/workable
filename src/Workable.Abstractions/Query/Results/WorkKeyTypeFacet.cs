namespace Workable;

/// <summary>
/// Represents a compact key-type facet across workers.
/// </summary>
/// <param name="Type">The caller-defined key type represented by the facet.</param>
/// <param name="WorkerCount">The number of matching workers in the facet.</param>
/// <param name="WorkerCountByKind">Worker counts within the facet, broken down by key kind.</param>
public sealed record WorkKeyTypeFacet(
    string Type,
    int WorkerCount,
    IReadOnlyDictionary<WorkKeyKind, int> WorkerCountByKind);
