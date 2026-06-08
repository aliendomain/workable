namespace Workable;

/// <summary>
/// Identifies one registered work definition.
/// </summary>
/// <param name="Value">The underlying GUID value.</param>
public readonly record struct WorkDefinitionId(Guid Value)
{
    /// <summary>
    /// Creates a new unique work-definition identifier.
    /// </summary>
    /// <returns>A new unique definition identifier.</returns>
    public static WorkDefinitionId New() => new(Guid.NewGuid());

    /// <summary>
    /// Formats the identifier as a canonical GUID string.
    /// </summary>
    /// <returns>The identifier formatted with dashes.</returns>
    public override string ToString() => this.Value.ToString("D");
}
