namespace Workable;
public sealed class WorkableSignalROptions
{
    public string HubPath { get; set; } = "/workable/realtime";

    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan DiagnosticsPublishInterval { get; set; } = TimeSpan.FromMilliseconds(750);

    public int EventSubscriptionCapacity { get; set; } = 16_384;

    public WorkEventOverflowBehavior EventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropWrite;

    public WorkEventOverflowBehavior WorkerOverviewEventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropOldest;

    public int WorkerOverviewResyncQueuedEventThreshold { get; set; } = 512;

    public TimeSpan BatchTimeWindow { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan LiveTimeWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MinimumTimeWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    public int EventMaxBatchSize { get; set; } = 512;
}
