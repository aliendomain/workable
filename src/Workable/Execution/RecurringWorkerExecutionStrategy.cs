namespace Workable;

internal sealed class RecurringWorkerExecutionStrategy(
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
        var consecutiveFailures = 0;

        while (true)
        {
            var recurrence = worker.GetConfiguration().Recurrence;
            if (!recurrence.IsEnabled)
            {
                var stoppedStatus = worker.CompleteStoppedRecurrence();
                this.WorkerEvents.CompletionRecorded(worker, stoppedStatus);
                return worker.ToCompletion(stoppedStatus);
            }

            var attemptLoop = await this.ExecuteAttemptLoop(worker, cancellationToken);
            if (attemptLoop.Completion is not null)
            {
                return attemptLoop.Completion;
            }

            var result = ToFinalResult(attemptLoop.RequiredAttempt);
            consecutiveFailures = result.HasErrors ? consecutiveFailures + 1 : 0;
            var shouldContinue = ShouldContinue(recurrence, result.HasErrors, consecutiveFailures, out var circuitOpened);
            var status = worker.CompleteRecurringIteration(result, shouldContinue);

            if (!shouldContinue)
            {
                if (circuitOpened && recurrence.RaiseCircuitBreakerOpenedEvent)
                {
                    this.WorkerEvents.RecurrenceCircuitOpened(worker);
                }

                this.WorkerEvents.CompletionRecorded(worker, status);
                return worker.ToCompletion(status);
            }

            if (result.HasErrors)
            {
                this.WorkerEvents.IterationFailed(worker);
            }
            else
            {
                this.WorkerEvents.IterationCompleted(worker);
            }

            this.WorkerEvents.Waiting(worker);
            recurrence = worker.GetConfiguration().Recurrence;
            if (!recurrence.IsEnabled)
            {
                var stoppedStatus = worker.CompleteStoppedRecurrence();
                this.WorkerEvents.CompletionRecorded(worker, stoppedStatus);
                return worker.ToCompletion(stoppedStatus);
            }

            await worker.WaitForRecurrenceInterval(recurrence.Interval, cancellationToken);

            recurrence = worker.GetConfiguration().Recurrence;
            if (!recurrence.IsEnabled)
            {
                var stoppedStatus = worker.CompleteStoppedRecurrence();
                this.WorkerEvents.CompletionRecorded(worker, stoppedStatus);
                return worker.ToCompletion(stoppedStatus);
            }

            if (!this.IterationTransitions.TryBeginNextRecurringIteration(worker))
            {
                return worker.ToCompletion(WorkerStateMachine.CompletionStatusFor(worker.State));
            }
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
