namespace Workable;
public sealed class WorkableSignalROptions
{
    public string HubPath { get; set; } = "/workable/realtime";

    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan DiagnosticsPublishInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int EventSubscriptionCapacity { get; set; } = 1_024;

    public WorkEventOverflowBehavior EventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropOldest;
}
