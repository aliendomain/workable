namespace Workable;

/// <summary>
/// Distinguishes whether a caller entered Workable through a host-defined application surface or a built-in Workable adapter surface.
/// </summary>
public enum WorkOriginSurface
{
    /// <summary>
    /// The caller entered Workable through a host-defined application surface such as direct .NET code or a custom endpoint.
    /// </summary>
    HostApplication,

    /// <summary>
    /// The caller entered Workable through a built-in Workable adapter surface such as the HTTP API, MCP server, or SignalR hub.
    /// </summary>
    WorkableAdapter,
}
