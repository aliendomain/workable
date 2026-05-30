namespace Workable;

public sealed record WorkerActionHistoryEntry(
    DateTimeOffset OccurredAt,
    WorkerActionHistoryKind Kind,
    WorkAction? Action,
    WorkActionStatus Status,
    WorkOrigin Origin,
    long Revision,
    long StateSequence,
    WorkerState State,
    IReadOnlyList<WorkMessage> Messages,
    long? IterationSequence = null,
    WorkerReconfiguration? Reconfiguration = null);
