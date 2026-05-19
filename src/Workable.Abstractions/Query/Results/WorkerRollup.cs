namespace Workable;

public sealed record WorkerRollup(
    int Total,
    int Active,
    int Queued,
    int Running,
    int Waiting,
    int Paused,
    int Failed,
    int Canceled,
    int Completed,
    DateTimeOffset? LastActivityAt);
