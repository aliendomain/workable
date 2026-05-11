namespace Workable;

internal sealed class WorkerExecutionCompletionRecorder(WorkerEventPublisher workerEvents)
{
    public WorkCompletion Complete(WorkerRecord worker, WorkExecutionResult result)
    {
        var status = worker.Complete(result);
        workerEvents.CompletionRecorded(worker, status);
        return worker.ToCompletion(status);
    }

    public WorkCompletion CompleteCancellation(WorkerRecord worker)
    {
        var status = worker.CompleteCancellation();
        if (status != WorkCompletionStatus.Invalid)
        {
            workerEvents.CompletionRecorded(worker, status);
        }

        return worker.ToCompletion(status);
    }

    public WorkCompletion Fail(WorkerRecord worker, WorkMessage message)
    {
        worker.Fail(message);
        workerEvents.Failed(worker);
        return worker.ToCompletion(WorkCompletionStatus.Failed);
    }
}
