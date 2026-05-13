namespace Workable;

public sealed record WorkIterationKeyTypeQuery(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyQuery.DefaultTake);
