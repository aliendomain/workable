using System.Text.Json.Serialization;

namespace Workable;

/// <summary>
/// Represents the per-request worker option overrides accepted by the HTTP queue contract.
/// </summary>
public sealed record WorkableHttpWorkerOptions
{
    /// <summary>
    /// Initializes queue-time HTTP worker options with inherited profiling behavior.
    /// </summary>
    public WorkableHttpWorkerOptions()
    {
    }

    /// <summary>
    /// Initializes queue-time HTTP worker options with configuration overrides while leaving profiling inherited.
    /// </summary>
    /// <param name="Configuration">Optional per-request runtime configuration overrides for the queued worker.</param>
    public WorkableHttpWorkerOptions(WorkableHttpWorkConfiguration? Configuration)
    {
        this.Configuration = Configuration;
    }

    /// <summary>
    /// Initializes queue-time HTTP worker options with an explicit profiling override.
    /// </summary>
    /// <param name="ProfilingEnabled">Whether profiling should be enabled for the queued worker.</param>
    /// <param name="Configuration">Optional per-request runtime configuration overrides for the queued worker.</param>
    public WorkableHttpWorkerOptions(
        bool ProfilingEnabled,
        WorkableHttpWorkConfiguration? Configuration = null)
    {
        this.ProfilingEnabled = ProfilingEnabled;
        this.Configuration = Configuration;
    }

    /// <summary>
    /// Gets or sets whether profiling should be enabled for the queued worker. Leave <see langword="null"/> to inherit the work definition or environment default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProfilingEnabled { get; init; }

    /// <summary>
    /// Gets or sets optional per-request runtime configuration overrides for the queued worker.
    /// </summary>
    public WorkableHttpWorkConfiguration? Configuration { get; init; }

    internal WorkerOptions ToWorkerOptions()
        => this.ProfilingEnabled is { } profilingEnabled
            ? new WorkerOptions(
                profilingEnabled,
                this.Configuration?.ToWorkConfiguration())
            : new WorkerOptions(this.Configuration?.ToWorkConfiguration());
}
