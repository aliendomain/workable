namespace Workable;
public sealed record WorkableHttpHostDescriptor(
    WorkableHttpHostCapabilities Capabilities,
    IReadOnlyList<WorkableHttpSystemDescriptor> Systems);
