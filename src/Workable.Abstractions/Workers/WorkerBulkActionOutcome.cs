namespace Workable;

/// <summary>
/// Represents the aggregate result of applying one action to many workers.
/// </summary>
/// <param name="Action">The action that was requested.</param>
/// <param name="Filter">The filter that selected the target worker set.</param>
/// <param name="MatchedWorkerCount">The number of workers matched before individual action outcomes were evaluated.</param>
/// <param name="Outcomes">The individual action outcomes for the matched workers.</param>
public sealed record WorkerBulkActionOutcome(
    WorkAction Action,
    WorkerBulkActionFilter Filter,
    int MatchedWorkerCount,
    IReadOnlyList<WorkActionOutcome> Outcomes)
{
    /// <summary>
    /// Gets the number of matched workers whose action request was accepted.
    /// </summary>
    public int AcceptedCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Accepted);

    /// <summary>
    /// Gets the number of matched workers whose action request conflicted.
    /// </summary>
    public int ConflictCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Conflict);

    /// <summary>
    /// Gets the number of matched workers whose action request was invalid.
    /// </summary>
    public int InvalidCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Invalid);

    /// <summary>
    /// Gets the number of matched workers whose action request was unauthorized.
    /// </summary>
    public int UnauthorizedCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Unauthorized);

    /// <summary>
    /// Gets the number of matched workers that were no longer found when the action was applied.
    /// </summary>
    public int NotFoundCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.NotFound);
}
