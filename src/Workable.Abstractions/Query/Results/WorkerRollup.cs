namespace Workable;

/// <summary>
/// Summarizes worker counts for one definition.
/// </summary>
/// <param name="Total">The total number of workers for the definition.</param>
/// <param name="Active">The number of workers in non-final states.</param>
/// <param name="Queued">The number of queued workers.</param>
/// <param name="Running">The number of running workers.</param>
/// <param name="Waiting">The number of waiting workers.</param>
/// <param name="Paused">The number of paused workers.</param>
/// <param name="Failed">The number of failed workers.</param>
/// <param name="Canceled">The number of canceled workers.</param>
/// <param name="Completed">The number of completed workers.</param>
/// <param name="LastActivityAt">The most recent worker activity time for the definition, when any worker exists.</param>
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
