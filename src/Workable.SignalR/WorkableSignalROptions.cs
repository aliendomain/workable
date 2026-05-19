namespace Workable;
public sealed class WorkableSignalROptions
{
    public string HubPath { get; set; } = "/workable/realtime";

    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan DiagnosticsPublishInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public int EventSubscriptionCapacity { get; set; } = 16_384;

    public WorkEventOverflowBehavior EventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropWrite;

    public TimeSpan EventBatchWindow { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan EventMinimumBatchWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    public int EventMaxBatchSize { get; set; } = 512;
}
