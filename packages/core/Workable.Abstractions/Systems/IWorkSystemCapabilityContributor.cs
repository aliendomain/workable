namespace Workable;

/// <summary>
/// Contributes optional system capabilities that should be advertised for hosted Workable systems.
/// </summary>
public interface IWorkSystemCapabilityContributor
{
    /// <summary>
    /// Applies capability flags to the shared capability snapshot for a system.
    /// </summary>
    /// <param name="capabilities">The mutable capability builder for the system.</param>
    void ConfigureCapabilities(WorkSystemCapabilitiesBuilder capabilities);
}
