namespace Workable;

public sealed record WorkIterationKeyTypeCriteria(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyCriteria.DefaultTake,
    IReadOnlySet<WorkDefinitionId>? DefinitionIds = null);
