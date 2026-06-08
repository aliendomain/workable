namespace Workable;

/// <summary>
/// Represents a compact iteration-count summary for a scoped system slice.
/// </summary>
/// <param name="CurrentIterationCount">The number of iterations that are currently executing.</param>
/// <param name="CompletedIterationCount">The number of retained completed iterations.</param>
/// <param name="FailedIterationCount">The number of retained failed iterations.</param>
/// <param name="CanceledIterationCount">The number of retained canceled iterations.</param>
/// <param name="IterationCountByStatus">Iteration counts grouped by completion status.</param>
public sealed record WorkSystemIterationCounts(
    int CurrentIterationCount,
    int CompletedIterationCount,
    int FailedIterationCount,
    int CanceledIterationCount,
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus) : IWorkQueryResult;
