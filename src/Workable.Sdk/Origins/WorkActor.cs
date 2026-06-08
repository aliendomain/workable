namespace Workable;

/// <summary>
/// Identifies the caller or principal that initiated a Workable action.
/// </summary>
/// <param name="Id">A stable identifier for the actor, when known.</param>
/// <param name="Name">A display name for the actor, when known.</param>
/// <param name="Email">An email address for the actor, when known.</param>
public sealed record WorkActor(
    string? Id = null,
    string? Name = null,
    string? Email = null)
{
    /// <summary>
    /// Gets a placeholder actor that represents an unknown caller.
    /// </summary>
    public static WorkActor Unknown { get; } = new();

    /// <summary>
    /// Gets a value indicating whether any identifying information is present for the actor.
    /// </summary>
    public bool IsKnown
        => !string.IsNullOrWhiteSpace(this.Id) ||
            !string.IsNullOrWhiteSpace(this.Name) ||
            !string.IsNullOrWhiteSpace(this.Email);
}
