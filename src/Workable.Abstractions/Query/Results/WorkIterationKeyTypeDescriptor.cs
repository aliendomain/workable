namespace Workable;

/// <summary>
/// Represents one grouped iteration key-type row.
/// </summary>
/// <param name="Type">The caller-defined key type represented by the group.</param>
/// <param name="IterationCount">The number of matching iterations in the group.</param>
/// <param name="IterationCountByKind">Iteration counts within the group, broken down by key kind.</param>
/// <param name="Iterations">Representative iteration overview rows for the group.</param>
public sealed record WorkIterationKeyTypeDescriptor(
    string Type,
    int IterationCount,
    IReadOnlyDictionary<WorkKeyKind, int> IterationCountByKind,
    IReadOnlyList<WorkerIterationOverviewItem> Iterations);
