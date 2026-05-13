namespace Workable;

internal sealed class RecurringWorkerExecutionStrategy(
    WorkerExecutionAttemptRunner attemptRunner,
    WorkerExecutionCompletionRecorder completionRecorder,
    WorkerEventPublisher workerEvents) : IWorkerExecutionStrategy
{
    public async Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;

        try
        {
            while (true)
            {
                var recurrence = worker.GetConfiguration().Recurrence;
                if (!recurrence.IsEnabled)
                {
                    var stoppedStatus = worker.CompleteStoppedRecurrence();
                    workerEvents.CompletionRecorded(worker, stoppedStatus);
                    return worker.ToCompletion(stoppedStatus);
                }

                var retryAttempts = 0;
                WorkerExecutionAttempt attempt;
                while (true)
                {
                    attempt = await attemptRunner.Execute(worker, retryAttempts, cancellationToken);
                    if (!attempt.IsExceptionFailure)
                    {
                        break;
                    }

                    var transientRetry = worker.GetConfiguration().TransientRetry;
                    if (attempt.RequiredExceptionClassification != WorkExceptionClassification.Transient ||
                        retryAttempts >= transientRetry.Count)
                    {
                        attemptRunner.LogFinalException(worker, attempt, retryAttempts);
                        break;
                    }

                    retryAttempts++;
                    var retryDelay = TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempts);
                    var retryResult = WorkExecutionResult.Failure([attempt.RequiredExceptionFailureMessage]);
                    worker.CompleteRetryIteration(retryResult, retryDelay);
                    workerEvents.IterationFailed(worker);
                    attemptRunner.LogRetrying(worker, attempt, retryAttempts, transientRetry.Count, retryDelay);
                    workerEvents.Retrying(worker, retryDelay);
                    await worker.WaitForRecurrenceInterval(retryDelay, cancellationToken);

                    if (!worker.TryBeginRetryIteration())
                    {
                        return worker.ToCompletion(WorkerStateMachine.CompletionStatusFor(worker.State));
                    }

                    workerEvents.Started(worker);
                }

                var result = attempt.IsExceptionFailure
                    ? WorkExecutionResult.Failure([attempt.RequiredExceptionFailureMessage])
                    : attempt.RequiredResult;
                consecutiveFailures = result.HasErrors ? consecutiveFailures + 1 : 0;
                var shouldContinue = ShouldContinue(recurrence, result.HasErrors, consecutiveFailures, out var circuitOpened);
                var status = worker.CompleteRecurringIteration(result, shouldContinue);

                if (!shouldContinue)
                {
                    if (circuitOpened && recurrence.RaiseCircuitBreakerOpenedEvent)
                    {
                        workerEvents.RecurrenceCircuitOpened(worker);
                    }

                    workerEvents.CompletionRecorded(worker, status);
                    return worker.ToCompletion(status);
                }

                if (result.HasErrors)
                {
                    workerEvents.IterationFailed(worker);
                }
                else
                {
                    workerEvents.IterationCompleted(worker);
                }

                workerEvents.Waiting(worker);
                recurrence = worker.GetConfiguration().Recurrence;
                if (!recurrence.IsEnabled)
                {
                    var stoppedStatus = worker.CompleteStoppedRecurrence();
                    workerEvents.CompletionRecorded(worker, stoppedStatus);
                    return worker.ToCompletion(stoppedStatus);
                }

                await worker.WaitForRecurrenceInterval(recurrence.Interval, cancellationToken);

                recurrence = worker.GetConfiguration().Recurrence;
                if (!recurrence.IsEnabled)
                {
                    var stoppedStatus = worker.CompleteStoppedRecurrence();
                    workerEvents.CompletionRecorded(worker, stoppedStatus);
                    return worker.ToCompletion(stoppedStatus);
                }

                if (!worker.TryBeginNextRecurringIteration())
                {
                    return worker.ToCompletion(WorkerStateMachine.CompletionStatusFor(worker.State));
                }

                workerEvents.Started(worker);
            }
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

    private static bool ShouldContinue(
        WorkRecurrenceConfiguration recurrence,
        bool hasErrors,
        int consecutiveFailures,
        out bool circuitOpened)
    {
        circuitOpened = false;
        if (!hasErrors)
        {
            return true;
        }

        if (!recurrence.ContinueAfterFailure)
        {
            return false;
        }

        circuitOpened = consecutiveFailures >= recurrence.CircuitBreakerFailureThreshold;
        return !circuitOpened;
    }
}
