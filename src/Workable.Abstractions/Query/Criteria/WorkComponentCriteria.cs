using System.Text.Json;

namespace Workable;

public sealed record WorkComponentCriteria(
    WorkOverviewCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkSingleComponentCriteria(
    WorkOverviewCriteria? Scope = null,
    JsonElement? Options = null);

public sealed record WorkViewCriteria(
    WorkOverviewCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkComponentRequest(
    string Id,
    string Type,
    JsonElement? Options = null);
