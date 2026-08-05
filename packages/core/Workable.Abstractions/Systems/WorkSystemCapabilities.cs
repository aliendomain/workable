namespace Workable;

/// <summary>
/// Describes the full set of optional capabilities available on a hosted Workable system.
/// </summary>
/// <param name="PersistentCoordinationAvailable">
/// Whether the system currently supports persistent coordination features such as durable queueing and persistence-backed idempotency.
/// </param>
/// <param name="SqlProfilingAvailable">
/// Whether the system currently has SQL profiling capability available for captured worker profiles.
/// </param>
public sealed record WorkSystemCapabilities(
    bool PersistentCoordinationAvailable,
    bool SqlProfilingAvailable)
{
    /// <summary>
    /// Gets whether the system currently has outbound HTTP client profiling capability available for captured worker profiles.
    /// </summary>
    public bool HttpClientProfilingAvailable { get; init; }

    /// <summary>
    /// Gets a capability snapshot with every known capability disabled.
    /// </summary>
    public static WorkSystemCapabilities None { get; } = new(false, false);
}
