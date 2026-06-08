namespace Workable;

/// <summary>
/// Represents one grouped worker key-type row.
/// </summary>
/// <param name="Type">The caller-defined key type represented by the group.</param>
/// <param name="WorkerCount">The number of matching workers in the group.</param>
/// <param name="WorkerCountByKind">Worker counts within the group, broken down by key kind.</param>
/// <param name="Workers">Representative worker overview rows for the group.</param>
public sealed record WorkerKeyTypeDescriptor(
    string Type,
    int WorkerCount,
    IReadOnlyDictionary<WorkKeyKind, int> WorkerCountByKind,
    IReadOnlyList<WorkerOverviewItem> Workers);
