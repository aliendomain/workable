namespace Workable;

public sealed record WorkerActionHistoryEntry(
    DateTimeOffset OccurredAt,
    WorkerActionHistoryKind Kind,
    WorkAction? Action,
    WorkActionStatus Status,
    WorkOrigin Origin,
    long Revision,
    long StateSequence,
    IReadOnlyList<WorkMessage> Messages,
    WorkerReconfiguration? Reconfiguration = null);
