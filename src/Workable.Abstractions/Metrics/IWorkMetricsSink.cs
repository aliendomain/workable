namespace Workable;

public interface IWorkMetricsSink
{
    void IterationRecorded(WorkDefinitionId definitionId, WorkerIterationSnapshot iteration);
}
