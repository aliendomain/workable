namespace Workable;

/// <summary>
/// Identifies one Workable system instance.
/// </summary>
/// <param name="Value">The underlying GUID value.</param>
public readonly record struct WorkSystemId(Guid Value)
{
    /// <summary>
    /// Creates a new unique system identifier.
    /// </summary>
    /// <returns>A new unique system identifier.</returns>
    public static WorkSystemId New() => new(Guid.NewGuid());

    /// <summary>
    /// Formats the identifier as a canonical GUID string.
    /// </summary>
    /// <returns>The identifier formatted with dashes.</returns>
    public override string ToString() => this.Value.ToString("D");
}
