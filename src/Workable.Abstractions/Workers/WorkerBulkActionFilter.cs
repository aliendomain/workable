namespace Workable;

/// <summary>
/// Filters the set of workers targeted by a bulk action request.
/// </summary>
/// <param name="Category">An optional category-path filter.</param>
/// <param name="IncludeSubcategories">Whether a category filter should include descendant category paths.</param>
public sealed record WorkerBulkActionFilter(
    string? Category = null,
    bool IncludeSubcategories = true)
{
    /// <summary>
    /// Gets a filter that targets all workers visible to the caller.
    /// </summary>
    public static WorkerBulkActionFilter All { get; } = new();
}
