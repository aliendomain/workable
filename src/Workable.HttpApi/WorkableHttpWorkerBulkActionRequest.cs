namespace Workable;

public sealed record WorkableHttpWorkerBulkActionRequest(
    string? Category = null,
    bool IncludeSubcategories = true)
{
    public WorkerBulkActionFilter ToFilter()
        => new(this.Category, this.IncludeSubcategories);
}
