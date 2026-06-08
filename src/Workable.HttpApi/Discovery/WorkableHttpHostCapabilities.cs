namespace Workable;

/// <summary>
/// Describes host-wide optional capabilities advertised by the HTTP API root.
/// </summary>
/// <param name="Realtime">The realtime transport capability visible to HTTP clients.</param>
public sealed record WorkableHttpHostCapabilities(
    WorkRealtimeCapability Realtime);
