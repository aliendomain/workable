namespace Workable;

/// <summary>
/// Configures which tools the ASP.NET Core MCP server exposes and how work-tool invocations behave.
/// </summary>
public sealed class WorkableMcpServerOptions
{
    /// <summary>
    /// Gets the default MCP server options.
    /// </summary>
    public static WorkableMcpServerOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether authored work definitions should be exposed as MCP work tools.
    /// </summary>
    public bool IncludeWorkTools { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether read-only query tools should be exposed.
    /// </summary>
    public bool IncludeQueryTools { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether worker-action and definition-reconfiguration tools should be exposed.
    /// </summary>
    public bool IncludeActionTools { get; set; } = true;

    /// <summary>
    /// Gets or sets how work definitions are projected into MCP work tools.
    /// </summary>
    public WorkableMcpToolCatalogOptions ToolCatalog { get; set; } = WorkableMcpToolCatalogOptions.Default;

    /// <summary>
    /// Gets or sets the default invocation behavior used by work tools.
    /// </summary>
    public WorkableMcpInvocationOptions Invocation { get; set; } = WorkableMcpInvocationOptions.Default;
}
