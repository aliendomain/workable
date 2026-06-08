namespace Workable;

/// <summary>
/// Identifies the broad kind of MCP tool exposed by the ASP.NET Core Workable server.
/// </summary>
public enum WorkableMcpServerToolKind
{
    /// <summary>
    /// A tool that queues an authored Workable definition.
    /// </summary>
    Work,

    /// <summary>
    /// A read-only tool that queries system state.
    /// </summary>
    Query,

    /// <summary>
    /// A tool that mutates workers or definition defaults.
    /// </summary>
    Action,
}
