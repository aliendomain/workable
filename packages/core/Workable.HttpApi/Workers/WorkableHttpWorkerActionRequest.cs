namespace Workable;

/// <summary>
/// Represents the HTTP request body for a single-worker action.
/// </summary>
/// <param name="Revision">The expected current worker revision used for optimistic concurrency.</param>
/// <param name="Description">An optional human-readable reason for requesting the worker action.</param>
public sealed record WorkableHttpWorkerActionRequest(
    long Revision,
    string? Description = null);
