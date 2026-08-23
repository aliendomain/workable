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
    /// Gets or sets a value indicating whether the optional Workable query-token middleware may promote a browser
    /// access token on the mapped hub endpoint.
    /// </summary>
    public bool PromoteAccessTokensFromQueryString { get; set; } = true;

    /// <summary>
    /// Gets or sets the query-string parameter name used by the optional Workable query-token middleware.
    /// </summary>
    public string AccessTokenQueryStringName { get; set; } = "access_token";

    /// <summary>
    /// Gets or sets the positive interval at which interval-required named views are recomputed and published.
    /// </summary>
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the positive interval at which diagnostics view subscriptions are recomputed and published.
    /// </summary>
    public TimeSpan DiagnosticsPublishInterval { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Gets or sets the positive maximum number of buffered events per active raw event subscription group.
    /// </summary>
    public int EventSubscriptionCapacity { get; set; } = 16_384;

    /// <summary>
    /// Gets or sets the overflow behavior used when a raw event subscription group reaches capacity.
    /// </summary>
    public WorkEventOverflowBehavior EventOverflowBehavior { get; set; } = WorkEventOverflowBehavior.DropWrite;

    /// <summary>
    /// Gets or sets the positive time window used to collect additional raw events into one batch.
    /// </summary>
    public TimeSpan BatchTimeWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the positive coalescing window used for worker-overview broadcasts.
    /// </summary>
    public TimeSpan LiveTimeWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the minimum positive time window the broadcaster will honor for batching or live coalescing.
    /// </summary>
    public TimeSpan MinimumTimeWindow { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or sets the positive maximum number of raw events included in one <c>workable.events</c> batch.
    /// </summary>
    public int EventMaxBatchSize { get; set; } = 512;

    /// <summary>
    /// Gets or sets the positive maximum number of subscriptions of each realtime kind that one connection may hold.
    /// Named views, raw events, and worker overviews are counted independently.
    /// </summary>
    public int MaximumSubscriptionsPerConnectionPerKind { get; set; } = 32;

    /// <summary>
    /// Gets or sets the positive maximum number of subscriptions of each realtime kind across all connections.
    /// Named views, raw events, and worker overviews are counted independently.
    /// </summary>
    public int MaximumSubscriptionsPerKind { get; set; } = 1_024;

    /// <summary>
    /// Gets or sets the positive maximum number of values accepted in each raw-event filter collection.
    /// Event types, definition names, and keys are bounded independently.
    /// </summary>
    public int MaximumEventFilterValuesPerField { get; set; } = 128;

    /// <summary>
    /// Gets or sets the positive maximum character length of one raw-event filter string.
    /// </summary>
    public int MaximumEventFilterValueLength { get; set; } = 1_024;
}
