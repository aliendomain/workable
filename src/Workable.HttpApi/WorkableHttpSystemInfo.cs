namespace Workable;
public sealed record WorkableHttpSystemInfo(
    WorkSystemId Id,
    string? Name,
    WorkSystemState State,
    bool IsDefault,
    WorkableHttpCapabilities Capabilities);
