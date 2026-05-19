namespace Workable;

public sealed record WorkableHttpWorkerOptions(
    bool ProfilingEnabled = false,
    WorkableHttpWorkConfiguration? Configuration = null)
{
    internal WorkerOptions ToWorkerOptions()
        => new(
            ProfilingEnabled,
            Configuration?.ToWorkConfiguration());
}
