namespace Workable;

/// <summary>
/// Represents one retained worker action or reconfiguration history entry.
/// </summary>
/// <param name="OccurredAt">The time the history entry occurred.</param>
/// <param name="Kind">Whether the entry represents a worker action or a reconfiguration.</param>
/// <param name="Action">The requested worker action, when the entry represents an action.</param>
/// <param name="Status">The outcome status of the action or reconfiguration request.</param>
/// <param name="RequestContext">The caller context recorded for the request.</param>
/// <param name="Revision">The worker revision observed when the entry was recorded.</param>
/// <param name="StateSequence">The worker state sequence observed when the entry was recorded.</param>
/// <param name="State">The worker state observed when the entry was recorded.</param>
/// <param name="Messages">The retained messages associated with the entry.</param>
/// <param name="IterationSequence">The related iteration sequence, when the entry targets a specific iteration.</param>
/// <param name="Reconfiguration">The requested reconfiguration, when the entry represents a reconfiguration.</param>
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
    /// <summary>
    /// Gets the origin metadata extracted from <see cref="RequestContext"/>.
    /// </summary>
    public WorkOrigin Origin => this.RequestContext.Origin;
}
