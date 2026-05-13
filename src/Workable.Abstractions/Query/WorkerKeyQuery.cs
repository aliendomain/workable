namespace Workable;

public sealed record WorkerKeyQuery(
    WorkKeyKind? Kind = null,
    string? Type = null,
    string? Value = null,
    string? Search = null,
    IReadOnlySet<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyQuery.DefaultTake)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 50;
}
