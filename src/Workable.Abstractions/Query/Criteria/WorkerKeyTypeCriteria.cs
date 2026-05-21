namespace Workable;

public sealed record WorkerKeyTypeCriteria(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlySet<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyCriteria.DefaultTake,
    IReadOnlySet<WorkDefinitionId>? DefinitionIds = null);
