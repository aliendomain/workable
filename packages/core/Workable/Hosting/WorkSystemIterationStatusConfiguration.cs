namespace Workable;

/// <summary>
/// Configures transient iteration status replay for one Workable system.
/// </summary>
public sealed record WorkSystemIterationStatusConfiguration
{
    /// <summary>
    /// Gets the default iteration status configuration.
    /// </summary>
    public static WorkSystemIterationStatusConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the maximum number of recent status items retained for replay per iteration.
    /// </summary>
    public int ReplayItemCapacity { get; init; } = 4_096;

    /// <summary>
    /// Gets the maximum combined UTF-8 type and JSON payload bytes retained for replay per iteration.
    /// </summary>
    public int ReplayPayloadByteCapacity { get; init; } = 4 * 1_024 * 1_024;

    /// <summary>
    /// Gets the maximum number of status items retained for replay across the entire system.
    /// </summary>
    public int SystemReplayItemCapacity { get; init; } = 65_536;

    /// <summary>
    /// Gets the maximum combined UTF-8 type and JSON payload bytes retained across the entire system.
    /// </summary>
    public int SystemReplayByteCapacity { get; init; } = 64 * 1_024 * 1_024;

    /// <summary>
    /// Gets the maximum UTF-8 JSON payload size accepted for one status item.
    /// </summary>
    public int MaximumPayloadBytes { get; init; } = 32 * 1_024;

    /// <summary>
    /// Gets the maximum UTF-8 size accepted for one application-defined status type.
    /// </summary>
    public int MaximumTypeBytes { get; init; } = 256;

    /// <summary>
    /// Gets the maximum number of active status subscriptions across the entire system.
    /// </summary>
    public int MaximumSubscriptions { get; init; } = 4_096;

    /// <summary>
    /// Gets the maximum number of active status subscriptions for one iteration.
    /// </summary>
    public int MaximumSubscriptionsPerIteration { get; init; } = 64;
}
