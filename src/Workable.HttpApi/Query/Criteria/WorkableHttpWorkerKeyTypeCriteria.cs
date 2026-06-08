namespace Workable;

/// <summary>
/// Represents the HTTP request shape for worker key-type queries.
/// </summary>
/// <param name="Kind">An optional key-kind filter.</param>
/// <param name="Search">An optional search string applied to key types.</param>
/// <param name="Type">An optional exact key-type filter.</param>
/// <param name="States">Optional worker states to include in the grouped results.</param>
/// <param name="Skip">The number of grouped rows to skip.</param>
/// <param name="Take">The requested page size.</param>
public sealed record WorkableHttpWorkerKeyTypeCriteria(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlyList<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyCriteria.DefaultTake)
{
    /// <summary>
    /// Converts the HTTP criteria into the core worker key-type query contract.
    /// </summary>
    /// <returns>The core worker key-type query criteria.</returns>
    public WorkerKeyTypeCriteria ToWorkerKeyTypeCriteria()
        => new(
            Kind: this.Kind,
            Search: this.Search,
            Type: this.Type,
            States: this.States?.ToHashSet(),
            Skip: this.Skip,
            Take: this.Take);
}
