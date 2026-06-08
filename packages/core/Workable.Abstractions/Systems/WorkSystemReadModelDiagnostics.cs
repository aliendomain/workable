namespace Workable;

/// <summary>
/// Describes the state of the projected read model used by aggregate query APIs.
/// </summary>
/// <param name="EnqueuedSequence">The latest lifecycle update sequence accepted by the projector.</param>
/// <param name="AppliedSequence">The latest lifecycle update sequence included in the published snapshot.</param>
/// <param name="AppliedUpdateCount">The total number of lifecycle updates applied by the projector.</param>
/// <param name="PublishedSnapshotCount">The total number of immutable snapshots published by the projector.</param>
/// <param name="LastBatchSize">The number of updates applied in the most recent projection batch.</param>
/// <param name="LastProjectionDuration">The duration of the most recent projection batch.</param>
/// <param name="LastProjectedAt">The time the most recent projection batch finished.</param>
/// <param name="ProjectorFailureType">The exception type from the most recent projector failure, when one occurred.</param>
/// <param name="ProjectorFailureMessage">The message from the most recent projector failure, when one occurred.</param>
public sealed record WorkSystemReadModelDiagnostics(
    long EnqueuedSequence,
    long AppliedSequence,
    long AppliedUpdateCount,
    long PublishedSnapshotCount,
    int LastBatchSize,
    TimeSpan LastProjectionDuration,
    DateTimeOffset? LastProjectedAt,
    string? ProjectorFailureType,
    string? ProjectorFailureMessage)
{
    /// <summary>
    /// Gets the number of accepted lifecycle updates that have not yet appeared in the published snapshot.
    /// </summary>
    public long PendingUpdateCount => Math.Max(0, this.EnqueuedSequence - this.AppliedSequence);

    /// <summary>
    /// Gets a value indicating whether the projector has recorded an internal failure.
    /// </summary>
    public bool HasProjectorFailure => this.ProjectorFailureType is not null;
}
