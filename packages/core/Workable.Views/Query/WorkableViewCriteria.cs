using System.Text.Json;

namespace Workable;

/// <summary>
/// Requests an arbitrary set of named components without binding to a built-in view name.
/// </summary>
/// <param name="Scope">Optional system scope applied to each requested component.</param>
/// <param name="Components">The components to materialize.</param>
public sealed record WorkComponentCriteria(
    WorkSystemCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

/// <summary>
/// Requests one component directly, including optional JSON options and a preferred shape.
/// </summary>
/// <param name="Scope">Optional system scope applied to the component query.</param>
/// <param name="Options">Optional component-specific JSON options.</param>
/// <param name="Shape">The preferred component shape.</param>
public sealed record WorkSingleComponentCriteria(
    WorkSystemCriteria? Scope = null,
    JsonElement? Options = null,
    string Shape = WorkComponentShapes.Detailed);

/// <summary>
/// Requests one named view with optional scope and component overrides.
/// </summary>
/// <param name="Scope">Optional system scope applied to the view.</param>
/// <param name="Components">
/// Optional component overrides. When omitted, the named view uses its built-in default component composition.
/// </param>
public sealed record WorkViewCriteria(
    WorkSystemCriteria? Scope = null,
    IReadOnlyList<WorkComponentRequest>? Components = null);

/// <summary>
/// Identifies one requested component within a view or component query.
/// </summary>
/// <param name="Id">The caller-defined stable id echoed back in the result map.</param>
/// <param name="Type">The canonical server-side component type name.</param>
/// <param name="Options">Optional component-specific JSON options.</param>
/// <param name="Shape">The preferred component shape.</param>
public sealed record WorkComponentRequest(
    string Id,
    string Type,
    JsonElement? Options = null,
    string Shape = WorkComponentShapes.Detailed);

/// <summary>
/// Defines the canonical component shape names used by the view contract.
/// </summary>
public static class WorkComponentShapes
{
    /// <summary>
    /// The smallest payload shape used for collapsed or summary-only UI states.
    /// </summary>
    public const string Compact = "compact";

    /// <summary>
    /// The normal payload shape used for summary panels.
    /// </summary>
    public const string Standard = "standard";

    /// <summary>
    /// The largest payload shape used for expanded tables and detailed panels.
    /// </summary>
    public const string Detailed = "detailed";
}
