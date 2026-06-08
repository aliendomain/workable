namespace Workable;

/// <summary>
/// Filters grouped worker key-type queries.
/// </summary>
/// <param name="Kind">An optional key kind filter.</param>
/// <param name="Search">An optional search string applied to key types.</param>
/// <param name="Type">An optional exact key-type filter.</param>
/// <param name="States">Optional worker states to include.</param>
/// <param name="Skip">The number of grouped results to skip.</param>
/// <param name="Take">The requested page size, capped by <see cref="WorkerKeyCriteria.MaximumTake"/>.</param>
/// <param name="DefinitionNames">Optional definition names to include.</param>
public sealed record WorkerKeyTypeCriteria(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlySet<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyCriteria.DefaultTake,
    IReadOnlySet<string>? DefinitionNames = null);
