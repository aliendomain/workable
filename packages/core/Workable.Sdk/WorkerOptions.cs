using System.Text.Json.Serialization;

namespace Workable;

/// <summary>
/// Supplies queue-time options and configuration overrides for one worker.
/// </summary>
public sealed record WorkerOptions
{
    private bool hasExplicitProfilingEnabled;
    private bool profilingEnabled;

    /// <summary>
    /// Initializes worker options without explicitly setting profiling.
    /// </summary>
    public WorkerOptions()
    {
    }

    /// <summary>
    /// Initializes worker options without explicitly setting profiling.
    /// </summary>
    /// <param name="Configuration">
    /// Optional runtime configuration overrides that Workable merges over the work definition defaults for this worker.
    /// </param>
    /// <param name="QueueDurabilityTransaction">
    /// Optional durability transaction context supplied by advanced durable queue integrations.
    /// </param>
    public WorkerOptions(
        WorkConfiguration? Configuration,
        IWorkQueueDurabilityTransaction? QueueDurabilityTransaction = null)
    {
        this.Configuration = Configuration;
        this.QueueDurabilityTransaction = QueueDurabilityTransaction;
    }

    /// <summary>
    /// Initializes worker options with an explicit profiling setting.
    /// </summary>
    /// <param name="ProfilingEnabled">Whether execution profiling should be captured for the worker.</param>
    /// <param name="Configuration">
    /// Optional runtime configuration overrides that Workable merges over the work definition defaults for this worker.
    /// </param>
    /// <param name="QueueDurabilityTransaction">
    /// Optional durability transaction context supplied by advanced durable queue integrations.
    /// </param>
    public WorkerOptions(
        bool ProfilingEnabled,
        WorkConfiguration? Configuration = null,
        IWorkQueueDurabilityTransaction? QueueDurabilityTransaction = null)
    {
        this.ProfilingEnabled = ProfilingEnabled;
        this.Configuration = Configuration;
        this.QueueDurabilityTransaction = QueueDurabilityTransaction;
    }

    /// <summary>
    /// Gets or sets whether execution profiling should be captured for the worker.
    /// </summary>
    public bool ProfilingEnabled
    {
        get => this.profilingEnabled;
        init
        {
            this.profilingEnabled = value;
            this.hasExplicitProfilingEnabled = true;
        }
    }

    /// <summary>
    /// Gets or sets optional runtime configuration overrides that Workable merges over the work definition defaults for this worker.
    /// </summary>
    public WorkConfiguration? Configuration { get; init; }

    /// <summary>
    /// Gets or sets the optional durability transaction context supplied by advanced durable queue integrations.
    /// </summary>
    public IWorkQueueDurabilityTransaction? QueueDurabilityTransaction { get; init; }

    /// <summary>
    /// Gets the default worker options with profiling left unset and no overrides applied.
    /// </summary>
    public static WorkerOptions Default { get; } = new();

    /// <summary>
    /// Gets whether <see cref="ProfilingEnabled"/> was explicitly set on this instance instead of being inherited.
    /// </summary>
    [JsonIgnore]
    public bool HasExplicitProfilingEnabled => this.hasExplicitProfilingEnabled;

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
                ProfilingEnabled = overrides.HasExplicitProfilingEnabled ? overrides.ProfilingEnabled : this.ProfilingEnabled,
                Configuration = this.Configuration?.MergeRuntimeOptions(overrides.Configuration) ?? overrides.Configuration,
                QueueDurabilityTransaction = overrides.QueueDurabilityTransaction,
            };
}
