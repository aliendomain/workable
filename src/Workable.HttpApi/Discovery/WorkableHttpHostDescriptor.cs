namespace Workable;

/// <summary>
/// Represents the host-level discovery payload returned by the HTTP API root.
/// </summary>
/// <param name="Capabilities">The host-wide optional capabilities visible to the caller.</param>
/// <param name="Systems">The systems visible to the caller after access filtering.</param>
public sealed record WorkableHttpHostDescriptor(
    WorkableHttpHostCapabilities Capabilities,
    IReadOnlyList<WorkableHttpSystemDescriptor> Systems);
