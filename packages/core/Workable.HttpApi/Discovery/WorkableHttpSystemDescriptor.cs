namespace Workable;

/// <summary>
/// Represents one system entry in the HTTP host discovery payload.
/// </summary>
/// <param name="Name">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
/// <param name="State">The current lifecycle state of the system.</param>
/// <param name="IsDefault">Whether this entry represents the default unnamed system.</param>
/// <param name="Capabilities">The system-specific capabilities visible to the caller.</param>
/// <param name="Access">The caller's effective system-level and definition-level access summary.</param>
public sealed record WorkableHttpSystemDescriptor(
    string? Name,
    WorkSystemState State,
    bool IsDefault,
    WorkSystemCapabilities Capabilities,
    WorkSystemAccessSummary Access);
