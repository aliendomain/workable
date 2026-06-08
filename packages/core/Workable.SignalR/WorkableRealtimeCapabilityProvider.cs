using Microsoft.Extensions.Options;

namespace Workable;
internal sealed class WorkableRealtimeCapabilityProvider(IOptions<WorkableSignalROptions> options) : IWorkRealtimeCapabilityProvider
{
    public WorkRealtimeCapability GetCapability()
    {
        return new WorkRealtimeCapability(
            Enabled: true,
            Transport: "signalr",
            HubPath: options.Value.HubPath);
    }
}
