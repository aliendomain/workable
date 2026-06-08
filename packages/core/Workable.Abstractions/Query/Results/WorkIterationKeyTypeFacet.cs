namespace Workable;

/// <summary>
/// Represents a compact key-type facet across retained iterations.
/// </summary>
/// <param name="Type">The caller-defined key type represented by the facet.</param>
/// <param name="IterationCount">The number of matching iterations in the facet.</param>
/// <param name="IterationCountByKind">Iteration counts within the facet, broken down by key kind.</param>
public sealed record WorkIterationKeyTypeFacet(
    string Type,
    int IterationCount,
    IReadOnlyDictionary<WorkKeyKind, int> IterationCountByKind);
