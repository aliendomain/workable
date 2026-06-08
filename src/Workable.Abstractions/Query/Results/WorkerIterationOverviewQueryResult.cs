namespace Workable;

/// <summary>
/// Represents a list-style result that returns compact worker-iteration overview rows.
/// </summary>
/// <param name="Iterations">The matching iteration overview rows.</param>
public sealed record WorkerIterationOverviewQueryResult(IReadOnlyList<WorkerIterationOverviewItem> Iterations) :
    WorkQueryListResult<WorkerIterationOverviewItem>(Iterations);
