namespace Workable;

public sealed record WorkableHttpWorkIterationKeyTypeQuery(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlyList<WorkCompletionStatus>? Statuses = null,
    int Skip = 0,
    int Take = WorkIterationKeyQuery.DefaultTake)
{
    public WorkIterationKeyTypeQuery ToWorkIterationKeyTypeQuery()
        => new(
            Kind: this.Kind,
            Search: this.Search,
            Type: this.Type,
            Statuses: this.Statuses?.ToHashSet(),
            Skip: this.Skip,
            Take: this.Take);
}
