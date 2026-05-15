using System.Text.Json;

namespace Workable;

public sealed record WorkComponentQuery(
    WorkOverviewQuery? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkSingleComponentQuery(
    WorkOverviewQuery? Scope = null,
    JsonElement? Options = null);

public sealed record WorkViewQuery(
    WorkOverviewQuery? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkComponentRequest(
    string Id,
    string Type,
    JsonElement? Options = null);

public sealed record WorkComponentQueryResult(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, WorkComponentResult> Components);

public sealed record WorkComponentResult(
    string Status,
    object? Data = null,
    string? Error = null);
