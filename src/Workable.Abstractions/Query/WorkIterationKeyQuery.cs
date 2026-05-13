namespace Workable;

public sealed record WorkIterationKeyQuery(
    WorkKeyKind? Kind = null,
    string? Type = null,
    string? Value = null,
    string? Search = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyQuery.DefaultTake)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 50;
}
