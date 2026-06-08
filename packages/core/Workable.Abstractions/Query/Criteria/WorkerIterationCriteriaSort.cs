namespace Workable;

/// <summary>
/// Identifies the fields that iteration queries can sort by.
/// </summary>
public enum WorkerIterationCriteriaSort
{
    /// <summary>
    /// Sort by iteration start time.
    /// </summary>
    StartedAt,

    /// <summary>
    /// Sort by iteration completion time.
    /// </summary>
    CompletedAt,

    /// <summary>
    /// Sort by iteration execution duration.
    /// </summary>
    ExecutionDuration,

    /// <summary>
    /// Sort by definition name.
    /// </summary>
    DefinitionName,

    /// <summary>
    /// Sort by completion status.
    /// </summary>
    Status,
}
