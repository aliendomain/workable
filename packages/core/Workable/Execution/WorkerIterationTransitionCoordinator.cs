namespace Workable;

internal sealed class WorkerIterationTransitionCoordinator(WorkerEventPublisher workerEvents)
{
    public void RecordWorkerStarted(WorkerRecord worker)
    {
        workerEvents.Started(worker);
        workerEvents.IterationStarted(worker);
    }

    public bool TryBeginRetryIteration(WorkerRecord worker)
    {
        if (!worker.TryBeginRetryIteration())
        {
            return false;
        }

        workerEvents.IterationStarted(worker);
        return true;
    }

    public bool TryBeginNextRecurringIteration(WorkerRecord worker)
    {
        if (!worker.TryBeginNextRecurringIteration())
        {
            return false;
        }

        workerEvents.IterationStarted(worker);
        return true;
    }
}
