namespace Workable;

public sealed record WorkerKeyCriteria(
    WorkKeyKind? Kind = null,
    string? Type = null,
    string? Value = null,
    string? Search = null,
    IReadOnlySet<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyCriteria.DefaultTake,
    IReadOnlySet<WorkDefinitionId>? DefinitionIds = null)
{
    public const int DefaultTake = 50;
    public const int MaximumTake = 50;
}
