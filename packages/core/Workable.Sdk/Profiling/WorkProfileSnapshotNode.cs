namespace Workable;

/// <summary>
/// Represents one node in a captured profile tree.
/// </summary>
/// <param name="MetricType">The kind of profile node.</param>
/// <param name="TreeMilliseconds">The total elapsed time covered by the node and its children.</param>
/// <param name="NodeMilliseconds">The elapsed time attributed directly to the node.</param>
/// <param name="Label">The display label for the node.</param>
/// <param name="Context">Optional structured context captured on the node.</param>
/// <param name="Children">The nested child nodes.</param>
public sealed record WorkProfileSnapshotNode(
    WorkProfileMetricType MetricType,
    long TreeMilliseconds,
    long NodeMilliseconds,
    string Label,
    object? Context,
    IReadOnlyList<WorkProfileSnapshotNode> Children);
