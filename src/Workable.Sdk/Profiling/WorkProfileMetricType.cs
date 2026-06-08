namespace Workable;

/// <summary>
/// Identifies the kind of node represented in a captured profile tree.
/// </summary>
public enum WorkProfileMetricType
{
    /// <summary>
    /// A method-style scope that captures input and optionally result context.
    /// </summary>
    MethodScope,

    /// <summary>
    /// A logical nested scope.
    /// </summary>
    Scope,

    /// <summary>
    /// A timed measurement scope.
    /// </summary>
    Timing,

    /// <summary>
    /// A non-timed informational entry.
    /// </summary>
    Metric
}
