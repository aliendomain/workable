using Microsoft.Extensions.Options;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeCapabilityProviderShould
{
    [Fact]
    public void ReportSignalRRealtimeCapabilityWithDefaultHubPath()
    {
        var provider = CreateProvider(new WorkableSignalROptions());

        var capability = provider.GetCapability();

        Assert.True(capability.Enabled);
        Assert.Equal("signalr", capability.Transport);
        Assert.Equal("/workable/realtime", capability.HubPath);
    }

    [Fact]
    public void ReportConfiguredHubPath()
    {
        var provider = CreateProvider(new WorkableSignalROptions
        {
            HubPath = "/custom/realtime",
        });

        var capability = provider.GetCapability();

        Assert.True(capability.Enabled);
        Assert.Equal("signalr", capability.Transport);
        Assert.Equal("/custom/realtime", capability.HubPath);
    }

    private static WorkableRealtimeCapabilityProvider CreateProvider(WorkableSignalROptions options)
        => new(Options.Create(options));
}
