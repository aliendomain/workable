namespace Workable;

public sealed record WorkActor(
    string? Id = null,
    string? Name = null,
    string? Email = null)
{
    public static WorkActor Unknown { get; } = new();
}
