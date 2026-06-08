namespace Workable;

/// <summary>
/// Declares the stable public name, category, and optional description for a work definition.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class WorkMetadataAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkMetadataAttribute"/> class.
    /// </summary>
    /// <param name="name">The stable public definition name.</param>
    /// <param name="category">The category path used for organization and discovery.</param>
    /// <param name="description">An optional consumer-facing description of the work definition.</param>
    public WorkMetadataAttribute(string name, string category, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        this.Name = name;
        this.Category = category;
        this.Description = description;
    }

    /// <summary>
    /// Gets the stable public definition name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the category path used for organization and discovery.
    /// </summary>
    public string Category { get; }

    /// <summary>
    /// Gets the optional consumer-facing description of the definition.
    /// </summary>
    public string? Description { get; }
}
