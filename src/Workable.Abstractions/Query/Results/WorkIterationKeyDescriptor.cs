namespace Workable;

/// <summary>
/// Represents one grouped iteration-key row.
/// </summary>
/// <param name="Kind">The kind of relationship key represented by the group.</param>
/// <param name="Type">The caller-defined key type represented by the group.</param>
/// <param name="Value">The key value represented by the group.</param>
/// <param name="Iterations">Representative iteration overview rows for the group.</param>
public sealed record WorkIterationKeyDescriptor(
    WorkKeyKind Kind,
    string Type,
    string Value,
    IReadOnlyList<WorkerIterationOverviewItem> Iterations);
