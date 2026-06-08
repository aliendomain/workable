namespace Workable;

/// <summary>
/// Represents the HTTP request body for per-worker runtime reconfiguration.
/// </summary>
/// <param name="Revision">The expected current worker revision used for optimistic concurrency.</param>
/// <param name="Changes">The runtime changes to apply to the worker.</param>
/// <param name="Description">An optional human-readable description recorded on the reconfiguration origin.</param>
public sealed record WorkableHttpWorkerReconfigurationRequest(
    long Revision,
    WorkerReconfiguration Changes,
    string? Description = null);
