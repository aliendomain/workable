namespace Workable;

/// <summary>
/// Filters grouped iteration key-type queries.
/// </summary>
/// <param name="Kind">An optional key kind filter.</param>
/// <param name="Search">An optional search string applied to key types.</param>
/// <param name="Type">An optional exact key-type filter.</param>
/// <param name="Statuses">Optional completion statuses to include.</param>
/// <param name="Skip">The number of grouped results to skip.</param>
/// <param name="Take">The requested page size, capped by <see cref="WorkIterationKeyCriteria.MaximumTake"/>.</param>
/// <param name="DefinitionNames">Optional definition names to include.</param>
public sealed record WorkIterationKeyTypeCriteria(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyCriteria.DefaultTake,
    IReadOnlySet<string>? DefinitionNames = null);
