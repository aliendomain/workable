namespace Workable;

public sealed record WorkableHttpWorkerReconfigurationRequest(
    long Revision,
    WorkerReconfiguration Changes,
    string? Description = null);
