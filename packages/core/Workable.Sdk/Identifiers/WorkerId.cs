namespace Workable;

/// <summary>
/// Identifies one worker instance.
/// </summary>
/// <param name="Value">The underlying GUID value.</param>
public readonly record struct WorkerId(Guid Value)
{
    /// <summary>
    /// Creates a new unique worker identifier.
    /// </summary>
    /// <returns>A new unique worker identifier.</returns>
    public static WorkerId New() => new(Guid.NewGuid());

    /// <summary>
    /// Formats the identifier as a canonical GUID string.
    /// </summary>
    /// <returns>The identifier formatted with dashes.</returns>
    public override string ToString() => this.Value.ToString("D");
}
