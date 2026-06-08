namespace Workable;

/// <summary>
/// Receives iteration-level metrics as work executes.
/// </summary>
public interface IWorkMetricsSink
{
    /// <summary>
    /// Records a completed or settled worker iteration.
    /// </summary>
    /// <param name="definitionId">The identifier of the related work definition.</param>
    /// <param name="iteration">The retained iteration snapshot to record.</param>
    void IterationRecorded(WorkDefinitionId definitionId, WorkerIterationSnapshot iteration);
}
