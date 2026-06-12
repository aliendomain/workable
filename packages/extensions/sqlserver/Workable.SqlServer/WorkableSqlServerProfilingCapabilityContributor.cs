namespace Workable.SqlServer;

internal sealed class WorkableSqlServerProfilingCapabilityContributor : IWorkSystemCapabilityContributor
{
    public void ConfigureCapabilities(WorkSystemCapabilitiesBuilder capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        capabilities.SqlProfilingAvailable = true;
    }
}
