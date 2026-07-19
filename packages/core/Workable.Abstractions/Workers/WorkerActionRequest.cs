namespace Workable;

/// <summary>
/// Describes a worker action request and the optional human-readable reason for requesting it.
/// </summary>
/// <param name="Action">The worker action to perform.</param>
/// <param name="Reason">The optional human-readable reason for requesting the action.</param>
public sealed record WorkerActionRequest(
    WorkAction Action,
    string? Reason = null);
