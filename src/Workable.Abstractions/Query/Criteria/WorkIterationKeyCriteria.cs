namespace Workable;

public sealed record WorkIterationKeyCriteria(
    WorkKeyKind? Kind = null,
    string? Type = null,
    string? Value = null,
    string? Search = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyCriteria.DefaultTake)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 50;
}
