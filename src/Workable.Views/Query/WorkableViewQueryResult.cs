namespace Workable;

public sealed record WorkComponentQueryResult(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, WorkComponentResult> Components);

public sealed record WorkComponentResult(
    string Status,
    object? Data = null,
    string? Error = null,
    string Shape = WorkComponentShapes.Detailed);
