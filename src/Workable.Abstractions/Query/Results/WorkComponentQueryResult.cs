namespace Workable;

public sealed record WorkComponentQueryResult(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, WorkComponentResult> Components) : IWorkQueryResult;
