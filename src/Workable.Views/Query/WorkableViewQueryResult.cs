namespace Workable;

/// <summary>
/// Represents the generated result for a view or component query.
/// </summary>
/// <param name="GeneratedAt">The time the component result map was generated.</param>
/// <param name="Components">The materialized component results keyed by the caller-supplied component id.</param>
public sealed record WorkComponentQueryResult(
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, WorkComponentResult> Components);

/// <summary>
/// Represents the result for one requested component.
/// </summary>
/// <param name="Status">The component status, typically <c>ok</c> or <c>error</c>.</param>
/// <param name="Data">The component payload when the component was generated successfully.</param>
/// <param name="Error">The component-level error message when generation failed.</param>
/// <param name="Shape">The normalized component shape that was actually served.</param>
public sealed record WorkComponentResult(
    string Status,
    object? Data = null,
    string? Error = null,
    string Shape = WorkComponentShapes.Detailed);
