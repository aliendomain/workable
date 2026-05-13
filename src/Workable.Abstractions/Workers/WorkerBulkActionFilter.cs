namespace Workable;

public sealed record WorkerBulkActionFilter(
    string? Category = null,
    bool IncludeSubcategories = true)
{
    public static WorkerBulkActionFilter All { get; } = new();
}
