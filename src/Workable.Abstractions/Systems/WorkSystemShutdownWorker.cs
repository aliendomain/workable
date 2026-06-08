namespace Workable;

/// <summary>
/// Represents the minimal worker information returned in a shutdown summary.
/// </summary>
/// <param name="Id">The worker identifier.</param>
/// <param name="DefinitionName">The registered definition name that produced the worker.</param>
/// <param name="DefinitionCategory">The category of the registered definition.</param>
/// <param name="State">The worker state observed during shutdown.</param>
/// <param name="SubjectId">The optional primary business subject associated with the worker.</param>
public sealed record WorkSystemShutdownWorker(
    WorkerId Id,
    string DefinitionName,
    string DefinitionCategory,
    WorkerState State,
    WorkSubjectId? SubjectId)
{
    /// <summary>
    /// Gets a display-friendly name for the shutdown summary row.
    /// </summary>
    public string Name => this.DefinitionName;

    /// <summary>
    /// Creates a shutdown summary row from a worker overview item.
    /// </summary>
    /// <param name="worker">The worker overview item to project.</param>
    /// <returns>The projected shutdown summary row.</returns>
    public static WorkSystemShutdownWorker From(WorkerOverviewItem worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.Category,
            worker.State,
            worker.SubjectId);

    /// <summary>
    /// Creates a shutdown summary row from a retained worker snapshot.
    /// </summary>
    /// <param name="worker">The worker snapshot to project.</param>
    /// <returns>The projected shutdown summary row.</returns>
    public static WorkSystemShutdownWorker From(WorkerSnapshot worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.DefinitionCategory,
            worker.State,
            worker.SubjectId);
}
