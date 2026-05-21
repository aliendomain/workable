namespace Workable;

public sealed record WorkerBulkActionOutcome(
    WorkAction Action,
    WorkerBulkActionFilter Filter,
    int MatchedWorkerCount,
    IReadOnlyList<WorkActionOutcome> Outcomes)
{
    public int AcceptedCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Accepted);

    public int ConflictCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Conflict);

    public int InvalidCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Invalid);

    public int UnauthorizedCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.Unauthorized);

    public int NotFoundCount => this.Outcomes.Count(outcome => outcome.Status == WorkActionStatus.NotFound);
}
