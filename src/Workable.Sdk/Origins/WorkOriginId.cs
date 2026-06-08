namespace Workable;

/// <summary>
/// Identifies one recorded work origin.
/// </summary>
/// <param name="Value">The underlying origin identifier value.</param>
public readonly record struct WorkOriginId(Guid Value)
{
    /// <summary>
    /// Creates a new origin identifier.
    /// </summary>
    /// <returns>A new unique origin identifier.</returns>
    public static WorkOriginId New() => new(Guid.NewGuid());

    /// <summary>
    /// Formats the identifier as a canonical GUID string.
    /// </summary>
    /// <returns>The identifier formatted with dashes.</returns>
    public override string ToString() => this.Value.ToString("D");
}
