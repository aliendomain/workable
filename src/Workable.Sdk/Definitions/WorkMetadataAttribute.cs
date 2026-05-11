namespace Workable;

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class WorkMetadataAttribute : Attribute
{
    public WorkMetadataAttribute(string name, string category, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        this.Name = name;
        this.Category = category;
        this.Description = description;
    }

    public string Name { get; }

    public string Category { get; }

    public string? Description { get; }
}
