namespace Workable;

internal sealed class TransientRetryWorkerExecutionStrategy(
    WorkerExecutionAttemptRunner attemptRunner,
    WorkerExecutionCompletionRecorder completionRecorder,
    WorkerEventPublisher workerEvents,
    WorkerIterationTransitionCoordinator iterationTransitions) : RetryCapableWorkerExecutionStrategy(
        attemptRunner,
        completionRecorder,
        workerEvents,
        iterationTransitions)
{
    protected override async Task<WorkCompletion> ExecuteCore(WorkerRecord worker, CancellationToken cancellationToken)
    {
        var attemptLoop = await this.ExecuteAttemptLoop(worker, cancellationToken);
        if (attemptLoop.Completion is not null)
        {
            return attemptLoop.Completion;
        }

        var attempt = attemptLoop.RequiredAttempt;
        return attempt.IsExceptionFailure
            ? this.CompletionRecorder.Fail(worker, attempt.RequiredExceptionFailureMessage)
            : this.CompletionRecorder.Complete(worker, attempt.RequiredResult);
    }
}
