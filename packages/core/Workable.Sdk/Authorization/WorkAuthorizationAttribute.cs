namespace Workable;

/// <summary>
/// Declares default discover, read, and operate groups for a work definition.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkAuthorizationAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the groups that may discover the work definition and its schemas.
    /// </summary>
    public string[]? DiscoverGroups { get; init; }

    /// <summary>
    /// Gets or sets the groups that may read the work definition and its query surfaces.
    /// </summary>
    public string[]? ReadGroups { get; init; }

    /// <summary>
    /// Gets or sets the groups that may queue and operate the work definition.
    /// </summary>
    public string[]? OperateGroups { get; init; }
}
