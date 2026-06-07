namespace Workable;

public sealed record WorkActor(
    string? Id = null,
    string? Name = null,
    string? Email = null)
{
    public static WorkActor Unknown { get; } = new();

    public bool IsKnown
        => !string.IsNullOrWhiteSpace(this.Id) ||
            !string.IsNullOrWhiteSpace(this.Name) ||
            !string.IsNullOrWhiteSpace(this.Email);
}
