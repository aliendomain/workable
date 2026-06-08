namespace Workable;

/// <summary>
/// Represents the HTTP request shape for work-iteration key-type queries.
/// </summary>
/// <param name="Kind">An optional key-kind filter.</param>
/// <param name="Search">An optional search string applied to key types.</param>
/// <param name="Type">An optional exact key-type filter.</param>
/// <param name="Statuses">Optional iteration statuses to include in the grouped results.</param>
/// <param name="Skip">The number of grouped rows to skip.</param>
/// <param name="Take">The requested page size.</param>
public sealed record WorkableHttpWorkIterationKeyTypeCriteria(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlyList<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyCriteria.DefaultTake)
{
    /// <summary>
    /// Converts the HTTP criteria into the core work-iteration key-type query contract.
    /// </summary>
    /// <returns>The core work-iteration key-type query criteria.</returns>
    public WorkIterationKeyTypeCriteria ToWorkIterationKeyTypeCriteria()
        => new(
            Kind: this.Kind,
            Search: this.Search,
            Type: this.Type,
            Statuses: this.Statuses?.ToHashSet(),
            Skip: this.Skip,
            Take: this.Take);
}
