namespace Workable;

internal sealed class WorkableHttpClientProfilingCapabilityContributor : IWorkSystemCapabilityContributor
{
    public void ConfigureCapabilities(WorkSystemCapabilitiesBuilder capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        capabilities.HttpClientProfilingAvailable = true;
    }
}
