namespace Workable;
/// <summary>
/// Supplies queue-time options and configuration overrides for one worker.
/// </summary>
/// <param name="ProfilingEnabled">Whether execution profiling should be captured for the worker.</param>
/// <param name="Configuration">
/// Optional runtime configuration overrides that Workable merges over the work definition defaults for this worker.
/// </param>
/// <param name="QueueDurabilityTransaction">
/// Optional durability transaction context supplied by advanced durable queue integrations.
/// </param>
public sealed record WorkerOptions(
    bool ProfilingEnabled = false,
    WorkConfiguration? Configuration = null,
    IWorkQueueDurabilityTransaction? QueueDurabilityTransaction = null)
{
    /// <summary>
    /// Gets the default worker options with profiling disabled and no overrides applied.
    /// </summary>
    public static WorkerOptions Default { get; } = new();

    /// <summary>
    /// Merges queue-time overrides over the current worker options instance.
    /// </summary>
    /// <param name="overrides">The overriding options to apply, or <see langword="null"/> to leave the current options unchanged.</param>
    /// <returns>A merged options instance that prefers explicit override values.</returns>
    public WorkerOptions Merge(WorkerOptions? overrides)
        => overrides is null
            ? this
            : this with
            {
                ProfilingEnabled = overrides.ProfilingEnabled,
                Configuration = this.Configuration?.MergeRuntimeOptions(overrides.Configuration) ?? overrides.Configuration,
                QueueDurabilityTransaction = overrides.QueueDurabilityTransaction,
            };
}
