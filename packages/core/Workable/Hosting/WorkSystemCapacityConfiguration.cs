namespace Workable;

/// <summary>
/// Configures host-level admission limits for workers in one Workable system.
/// </summary>
public sealed record WorkSystemCapacityConfiguration
{
    /// <summary>
    /// Gets the default system capacity configuration.
    /// </summary>
    public static WorkSystemCapacityConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the maximum number of workers the system can retain in memory at once.
    /// </summary>
    public int MaximumWorkers { get; init; } = 1_000_000;
}
