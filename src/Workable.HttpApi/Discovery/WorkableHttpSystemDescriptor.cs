namespace Workable;
public sealed record WorkableHttpSystemDescriptor(
    string? Name,
    WorkSystemState State,
    bool IsDefault,
    WorkableHttpSystemCapabilities Capabilities,
    WorkSystemAccessSummary Access);
