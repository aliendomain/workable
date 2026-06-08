namespace Workable;

/// <summary>
/// Represents the HTTP request body for a bulk worker action.
/// </summary>
/// <param name="Category">An optional definition category filter used to narrow the matched workers.</param>
/// <param name="IncludeSubcategories">Whether category filtering includes descendant categories.</param>
/// <param name="Description">An optional human-readable description recorded on the bulk action origin.</param>
public sealed record WorkableHttpWorkerBulkActionRequest(
    string? Category = null,
    bool IncludeSubcategories = true,
    string? Description = null)
{
    /// <summary>
    /// Converts the HTTP request into the core bulk-action filter contract.
    /// </summary>
    /// <returns>The core bulk-action filter.</returns>
    public WorkerBulkActionFilter ToFilter()
        => new(this.Category, this.IncludeSubcategories);
}
