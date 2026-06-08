namespace Workable;

/// <summary>
/// Represents the per-request worker option overrides accepted by the HTTP queue contract.
/// </summary>
/// <param name="ProfilingEnabled">Whether profiling should be enabled for the queued worker.</param>
/// <param name="Configuration">Optional per-request runtime configuration overrides for the queued worker.</param>
public sealed record WorkableHttpWorkerOptions(
    bool ProfilingEnabled = false,
    WorkableHttpWorkConfiguration? Configuration = null)
{
    internal WorkerOptions ToWorkerOptions()
        => new(
            ProfilingEnabled,
            Configuration?.ToWorkConfiguration());
}
