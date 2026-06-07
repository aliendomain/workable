namespace Workable;

public sealed record WorkableHttpWorkerActionRequest(
    long Revision,
    string? Description = null);
