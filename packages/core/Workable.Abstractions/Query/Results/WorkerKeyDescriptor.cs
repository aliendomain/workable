namespace Workable;

/// <summary>
/// Represents one grouped worker-key row.
/// </summary>
/// <param name="Kind">The kind of relationship key represented by the group.</param>
/// <param name="Type">The caller-defined key type represented by the group.</param>
/// <param name="Value">The key value represented by the group.</param>
/// <param name="Workers">Representative worker overview rows for the group.</param>
public sealed record WorkerKeyDescriptor(
    WorkKeyKind Kind,
    string Type,
    string Value,
    IReadOnlyList<WorkerOverviewItem> Workers);
