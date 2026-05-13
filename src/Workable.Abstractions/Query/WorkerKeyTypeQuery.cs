namespace Workable;

public sealed record WorkerKeyTypeQuery(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlySet<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyQuery.DefaultTake);
