using System.Text.Json;

namespace Workable;

public sealed record WorkComponentCriteria(
    WorkSystemCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkSingleComponentCriteria(
    WorkSystemCriteria? Scope = null,
    JsonElement? Options = null);

public sealed record WorkViewCriteria(
    WorkSystemCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkComponentRequest(
    string Id,
    string Type,
    JsonElement? Options = null);
