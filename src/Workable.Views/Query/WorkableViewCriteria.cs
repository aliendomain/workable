using System.Text.Json;

namespace Workable;

public sealed record WorkComponentCriteria(
    WorkSystemCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkSingleComponentCriteria(
    WorkSystemCriteria? Scope = null,
    JsonElement? Options = null,
    string Shape = WorkComponentShapes.Detailed);

public sealed record WorkViewCriteria(
    WorkSystemCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

public sealed record WorkComponentRequest(
    string Id,
    string Type,
    JsonElement? Options = null,
    string Shape = WorkComponentShapes.Detailed);

public static class WorkComponentShapes
{
    public const string Compact = "compact";
    public const string Standard = "standard";
    public const string Detailed = "detailed";
}
