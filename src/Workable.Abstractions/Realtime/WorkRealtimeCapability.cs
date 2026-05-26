namespace Workable;
public sealed record WorkRealtimeCapability(
    bool Enabled,
    string? Transport = null,
    string? HubPath = null)
{
    public static WorkRealtimeCapability Disabled { get; } = new(false);
}
