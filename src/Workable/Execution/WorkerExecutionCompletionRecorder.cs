namespace Workable;

internal sealed class WorkerExecutionCompletionRecorder(WorkerEventPublisher workerEvents)
{
    public WorkCompletion Complete(WorkerRecord worker, WorkExecutionResult result)
    {
        var status = worker.Complete(result);
        if (status == WorkCompletionStatus.Completed)
        {
            workerEvents.IterationCompleted(worker);
        }
        else if (status == WorkCompletionStatus.Failed)
        {
            workerEvents.IterationFailed(worker);
        }

        workerEvents.CompletionRecorded(worker, status);
        return worker.ToCompletion(status);
    }

    public WorkCompletion CompleteCancellation(WorkerRecord worker)
    {
        var status = worker.IsInterrupted
            ? worker.CompleteInterruption()
            : worker.CompleteCancellation();
        if (status != WorkCompletionStatus.Invalid)
        {
            workerEvents.CompletionRecorded(worker, status);
        }

        return worker.ToCompletion(status);
    }

    public WorkCompletion Fail(WorkerRecord worker, WorkMessage message)
    {
        worker.Fail(message);
        workerEvents.IterationFailed(worker);
        workerEvents.Failed(worker);
        return worker.ToCompletion(WorkCompletionStatus.Failed);
    }
}
