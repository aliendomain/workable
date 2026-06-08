namespace Workable;

/// <summary>
/// Filters grouped iteration-key queries.
/// </summary>
/// <param name="Kind">An optional key kind filter.</param>
/// <param name="Type">An optional exact key-type filter.</param>
/// <param name="Value">An optional exact key-value filter.</param>
/// <param name="Search">An optional search string applied to key type and value.</param>
/// <param name="Statuses">Optional completion statuses to include.</param>
/// <param name="Skip">The number of grouped results to skip.</param>
/// <param name="Take">The requested page size, capped by <see cref="MaximumTake"/>.</param>
/// <param name="DefinitionNames">Optional definition names to include.</param>
public sealed record WorkIterationKeyCriteria(
    WorkKeyKind? Kind = null,
    string? Type = null,
    string? Value = null,
    string? Search = null,
    IReadOnlySet<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyCriteria.DefaultTake,
    IReadOnlySet<string>? DefinitionNames = null)
{
    /// <summary>
    /// The default page size for grouped iteration-key queries.
    /// </summary>
    public const int DefaultTake = 50;

    /// <summary>
    /// The maximum allowed page size for grouped iteration-key queries.
    /// </summary>
    public const int MaximumTake = 50;
}
