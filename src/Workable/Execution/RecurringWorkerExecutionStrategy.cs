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

                var attempt = await attemptRunner.Execute(
                    worker,
                    allowTransientRetry: worker.GetConfiguration().TransientRetry.Count > 0,
                    cancellationToken);

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
                    workerEvents.RecurringIterationFailed(worker);
                }
                else
                {
                    workerEvents.RecurringIterationCompleted(worker);
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
