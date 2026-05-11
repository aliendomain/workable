namespace Workable;

public sealed record WorkProfileSnapshotNode(
    WorkProfileMetricType MetricType,
    long TreeMilliseconds,
    long NodeMilliseconds,
    string Label,
    object? Context,
    IReadOnlyList<WorkProfileSnapshotNode> Children);
