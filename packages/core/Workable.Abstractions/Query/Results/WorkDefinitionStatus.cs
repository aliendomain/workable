namespace Workable;

/// <summary>
/// Represents the health-oriented status of a registered definition in summary views.
/// </summary>
public enum WorkDefinitionStatus
{
    /// <summary>
    /// The definition currently has no active workers.
    /// </summary>
    Inactive,

    /// <summary>
    /// The definition is operating normally.
    /// </summary>
    Healthy,

    /// <summary>
    /// The definition has signals that may need operator review.
    /// </summary>
    NeedsAttention,

    /// <summary>
    /// The definition has severe signals that likely require operator intervention.
    /// </summary>
    Critical,

    /// <summary>
    /// Workable could not determine a status for the definition.
    /// </summary>
    Unknown,
}
