namespace Workable;

/// <summary>
/// Configures the Workable SignalR hub path and realtime broadcast behavior.
/// </summary>
public sealed class WorkableSignalROptions
{
    /// <summary>
    /// Gets or sets the path used when the hub is mapped without an explicit override.
    /// </summary>
    public string HubPath { get; set; } = "/workable/realtime";

    /// <summary>
    /// Gets or sets how often interval-required named views are recomputed and published.
    /// </summary>
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets how often diagnostics view subscriptions are recomputed and published.
    /// </summary>
    public TimeSpan DiagnosticsPublishInterval { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Gets or sets the maximum number of buffered events per active raw event subscription group.
    /// </summary>
    public int EventSubscriptionCapacity { get; set; } = 16_384;

    /// <summary>
    /// Gets or sets the overflow behavior used when a raw event subscription group reaches capacity.
    /// </summary>
    public WorkEventOverflowBehavior EventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropWrite;

    /// <summary>
    /// Gets or sets the overflow behavior used when a worker-overview subscription backlog reaches capacity.
    /// </summary>
    public WorkEventOverflowBehavior WorkerOverviewEventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropOldest;

    /// <summary>
    /// Gets or sets the queued source-event threshold that causes worker-overview subscriptions to request a resync.
    /// </summary>
    public int WorkerOverviewResyncQueuedEventThreshold { get; set; } = 512;

    /// <summary>
    /// Gets or sets the time window used to collect additional raw events into one batch.
    /// </summary>
    public TimeSpan BatchTimeWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the coalescing window used for worker-overview delta broadcasts.
    /// </summary>
    public TimeSpan LiveTimeWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the minimum positive time window the broadcaster will honor for batching or live coalescing.
    /// </summary>
    public TimeSpan MinimumTimeWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the maximum number of raw events included in one <c>workable.events</c> batch.
    /// </summary>
    public int EventMaxBatchSize { get; set; } = 512;
}
