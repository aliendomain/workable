namespace Workable;
internal sealed class RunOnceWorkerExecutionStrategy(
    WorkerExecutionAttemptRunner attemptRunner,
    WorkerExecutionCompletionRecorder completionRecorder) : IWorkerExecutionStrategy
{
    public async Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        try
        {
            var attempt = await attemptRunner.Execute(worker, allowTransientRetry: false, cancellationToken);
            return attempt.IsExceptionFailure
                ? completionRecorder.Fail(worker, attempt.RequiredExceptionFailureMessage)
                : completionRecorder.Complete(worker, attempt.RequiredResult);
        }
        catch (OperationCanceledException)
        {
            return completionRecorder.CompleteCancellation(worker);
        }
        finally
        {
            worker.DisposeExecutionResources();
        }
    }
}
