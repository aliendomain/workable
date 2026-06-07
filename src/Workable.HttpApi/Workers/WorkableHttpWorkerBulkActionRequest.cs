namespace Workable;

public sealed record WorkableHttpWorkerBulkActionRequest(
    string? Category = null,
    bool IncludeSubcategories = true,
    string? Description = null)
{
    public WorkerBulkActionFilter ToFilter()
        => new(this.Category, this.IncludeSubcategories);
}
