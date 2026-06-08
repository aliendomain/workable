namespace Workable;

/// <summary>
/// Describes the realtime capabilities available to callers.
/// </summary>
/// <param name="Enabled">Whether realtime delivery is available.</param>
/// <param name="Transport">The transport name, when one is available.</param>
/// <param name="HubPath">The hub or endpoint path, when one is available.</param>
public sealed record WorkRealtimeCapability(
    bool Enabled,
    string? Transport = null,
    string? HubPath = null)
{
    /// <summary>
    /// Gets a capability value that indicates realtime delivery is unavailable.
    /// </summary>
    public static WorkRealtimeCapability Disabled { get; } = new(false);
}
