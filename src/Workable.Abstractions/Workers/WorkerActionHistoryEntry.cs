namespace Workable;

public sealed record WorkerActionHistoryEntry(
    DateTimeOffset OccurredAt,
    WorkerActionHistoryKind Kind,
    WorkAction? Action,
    WorkActionStatus Status,
    WorkRequestContext RequestContext,
    long Revision,
    long StateSequence,
    WorkerState State,
    IReadOnlyList<WorkMessage> Messages,
    long? IterationSequence = null,
    WorkerReconfiguration? Reconfiguration = null)
{
    public WorkOrigin Origin => this.RequestContext.Origin;
}
