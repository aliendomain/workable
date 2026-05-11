namespace Workable;

public sealed class WorkableMcpServerOptions
{
    public static WorkableMcpServerOptions Default { get; } = new();

    public bool IncludeWorkTools { get; set; } = true;

    public bool IncludeQueryTools { get; set; } = true;

    public bool IncludeActionTools { get; set; } = true;

    public WorkableMcpToolCatalogOptions ToolCatalog { get; set; } = WorkableMcpToolCatalogOptions.Default;

    public WorkableMcpInvocationOptions Invocation { get; set; } = WorkableMcpInvocationOptions.Default;
}
