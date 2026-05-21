using Microsoft.Extensions.Options;

namespace Workable;
internal sealed class WorkableRealtimeCapabilityProvider(IOptions<WorkableSignalROptions> options) : IWorkRealtimeCapabilityProvider
{
    private static readonly string[] Features =
    [
        "system-view",
        "work-views",
        "worker-events",
        "diagnostics-view",
    ];

    public WorkRealtimeCapability GetCapability(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return new WorkRealtimeCapability(
            Enabled: true,
            Transport: "signalr",
            HubPath: options.Value.HubPath,
            Features: Features);
    }
}
