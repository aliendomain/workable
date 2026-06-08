namespace Workable;

/// <summary>
/// Identifies the fields that worker queries can sort by.
/// </summary>
public enum WorkerCriteriaSort
{
    /// <summary>
    /// Sort by worker creation time.
    /// </summary>
    CreatedAt,

    /// <summary>
    /// Sort by worker last update time.
    /// </summary>
    UpdatedAt,

    /// <summary>
    /// Sort by definition name.
    /// </summary>
    DefinitionName,

    /// <summary>
    /// Sort by worker state.
    /// </summary>
    State,
}
