namespace Workable;

/// <summary>
/// Identifies an entry point that may start a work definition.
/// </summary>
public enum WorkInvocationChannel
{
    /// <summary>
    /// Direct in-process queueing through the .NET API.
    /// </summary>
    InProcess,

    /// <summary>
    /// The Workable HTTP API adapter.
    /// </summary>
    HttpApi,

    /// <summary>
    /// The Workable MCP adapter.
    /// </summary>
    Mcp,

    /// <summary>
    /// The Workable SignalR adapter.
    /// </summary>
    SignalR,
}
