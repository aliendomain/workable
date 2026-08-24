namespace Workable;
internal sealed class WorkableRealtimeCapabilityProvider(WorkableSignalRRegistration registration) : IWorkRealtimeCapabilityProvider
{
    public WorkRealtimeCapability GetCapability()
    {
        var hubPath = registration.AdvertisedHubPath;
        if (hubPath is null)
        {
            return WorkRealtimeCapability.Disabled;
        }

        return new WorkRealtimeCapability(
            Enabled: true,
            Transport: "signalr",
            HubPath: hubPath);
    }
}
