namespace Workable;

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
    public long PendingUpdateCount => Math.Max(0, this.EnqueuedSequence - this.AppliedSequence);

    public bool HasProjectorFailure => this.ProjectorFailureType is not null;
}
