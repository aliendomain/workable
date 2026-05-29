namespace Workable;

public sealed record WorkableHttpWorkerConfiguration(
    bool ProfilingEnabled,
    WorkableHttpWorkConfiguration Configuration);
