namespace Workable;

/// <summary>
/// Configures what Workable should do with workers that remain in the <c>Failed</c> state.
/// </summary>
public sealed record WorkFailedWorkerConfiguration
{
    /// <summary>
    /// Gets the default failed-worker handling configuration.
    /// </summary>
    public static WorkFailedWorkerConfiguration Default { get; } = new();

    /// <summary>
    /// Gets whether failed workers require manual handling or may be auto-canceled.
    /// </summary>
    public WorkFailedWorkerHandling Handling { get; init; } = WorkFailedWorkerHandling.Manual;

    /// <summary>
    /// Gets how long a worker may remain failed before auto-cancel occurs when auto-cancel handling is enabled.
    /// </summary>
    public TimeSpan AutoCancelAfter { get; init; } = TimeSpan.FromMinutes(10);
}
