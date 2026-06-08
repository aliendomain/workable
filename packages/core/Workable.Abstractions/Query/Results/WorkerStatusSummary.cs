namespace Workable;

/// <summary>
/// Represents a compact status-count summary for workers in a scoped query.
/// </summary>
/// <param name="Total">The total number of matching workers.</param>
/// <param name="Active">The number of matching workers in an active state.</param>
/// <param name="Final">The number of matching workers in a final state.</param>
/// <param name="Counts">Worker counts grouped by worker state.</param>
public sealed record WorkerStatusSummary(
    int Total,
    int Active,
    int Final,
    IReadOnlyDictionary<WorkerState, int> Counts) : IWorkQueryResult;
