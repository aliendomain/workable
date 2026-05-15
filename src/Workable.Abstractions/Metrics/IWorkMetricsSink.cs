namespace Workable;

public interface IWorkMetricsSink
{
    void WorkerQueued(WorkDefinitionId definitionId, DateTimeOffset queuedAt);

    void IterationCompleted(WorkDefinitionId definitionId, WorkerIterationSnapshot iteration);
}
