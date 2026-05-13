namespace Workable;

public sealed record WorkableHttpWorkerKeyTypeQuery(
    WorkKeyKind? Kind = null,
    string? Search = null,
    string? Type = null,
    IReadOnlyList<WorkerState>? States = null,
    int Skip = 0,
    int Take = WorkerKeyQuery.DefaultTake)
{
    public WorkerKeyTypeQuery ToWorkerKeyTypeQuery()
        => new(
            Kind: this.Kind,
            Search: this.Search,
            Type: this.Type,
            States: this.States?.ToHashSet(),
            Skip: this.Skip,
            Take: this.Take);
}
