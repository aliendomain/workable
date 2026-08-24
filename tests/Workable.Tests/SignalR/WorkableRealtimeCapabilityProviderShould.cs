using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeCapabilityProviderShould
{
    [Fact]
    public void ReportRealtimeDisabledUntilAHubMappingIsAdvertised()
    {
        var provider = CreateProvider();

        var capability = provider.GetCapability();

        Assert.Equal(WorkRealtimeCapability.Disabled, capability);
    }

    [Fact]
    public void ReportTheAdvertisedHubPath()
    {
        var registration = new WorkableSignalRRegistration();
        var provider = new WorkableRealtimeCapabilityProvider(registration);
        registration.Advertise("/custom/realtime");

        var capability = provider.GetCapability();

        Assert.True(capability.Enabled);
        Assert.Equal("signalr", capability.Transport);
        Assert.Equal("/custom/realtime", capability.HubPath);
    }

    [Fact]
    public void ReportMappedHubPathWithoutChangingConfiguredOptions()
    {
        var options = new WorkableSignalROptions();
        var registration = new WorkableSignalRRegistration();
        var provider = new WorkableRealtimeCapabilityProvider(registration);

        registration.Advertise("/mapped/realtime");

        Assert.Equal("/mapped/realtime", provider.GetCapability().HubPath);
        Assert.Equal("/workable/realtime", options.HubPath);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            registration.Advertise("/another/realtime"));
        Assert.Contains("advertised", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseBroadcasterStartupOnlyAfterAHubIsMapped()
    {
        var registration = new WorkableSignalRRegistration();

        var mapped = registration.WaitUntilMapped(CancellationToken.None);

        Assert.False(mapped.IsCompleted);
        registration.MarkMapped();
        await mapped;
        Assert.Equal(WorkRealtimeCapability.Disabled, new WorkableRealtimeCapabilityProvider(registration).GetCapability());
    }

    private static WorkableRealtimeCapabilityProvider CreateProvider()
        => new(new WorkableSignalRRegistration());
}
