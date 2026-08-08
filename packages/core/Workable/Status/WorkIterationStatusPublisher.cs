namespace Workable;

internal sealed class WorkIterationStatusPublisher(
    WorkIterationStatusStream stream,
    WorkerIterationReference iteration,
    string workDefinitionName) : IWorkIterationStatusPublisher
{
    public void Publish(WorkIterationStatusUpdate update)
        => stream.Publish(iteration, workDefinitionName, update);
}
