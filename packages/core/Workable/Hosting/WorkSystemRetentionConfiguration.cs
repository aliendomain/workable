namespace Workable;

/// <summary>
/// Configures host-level retention limits for final workers in one Workable system.
/// </summary>
public sealed record WorkSystemRetentionConfiguration
{
    /// <summary>
    /// Gets the default system retention configuration.
    /// </summary>
    public static WorkSystemRetentionConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the maximum number of completed or canceled workers retained across the whole system.
    /// </summary>
    public int MaximumFinalWorkers { get; init; } = 10_000;
}
