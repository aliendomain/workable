namespace Workable;

internal sealed class TransientRetryWorkerExecutionStrategy(
    WorkerExecutionAttemptRunner attemptRunner,
    WorkerExecutionCompletionRecorder completionRecorder,
    WorkerEventPublisher workerEvents) : IWorkerExecutionStrategy
{
    public async Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        try
        {
            var retryAttempts = 0;
            var transientRetry = worker.GetConfiguration().TransientRetry;

            while (true)
            {
                var attempt = await attemptRunner.Execute(worker, retryAttempts, cancellationToken);
                if (!attempt.IsExceptionFailure && !attempt.IsTransientDeclarativeFailure)
                {
                    return completionRecorder.Complete(worker, attempt.RequiredResult);
                }

                if (attempt.IsExceptionFailure &&
                    (attempt.RequiredExceptionClassification != WorkExceptionClassification.Transient ||
                    retryAttempts >= transientRetry.Count))
                {
                    attemptRunner.LogFinalException(worker, attempt, retryAttempts);
                    return completionRecorder.Fail(worker, attempt.RequiredExceptionFailureMessage);
                }

                if (attempt.IsTransientDeclarativeFailure &&
                    retryAttempts >= transientRetry.Count)
                {
                    return completionRecorder.Complete(worker, attempt.RequiredResult);
                }

                retryAttempts++;
                var delay = GetRetryDelay(transientRetry, retryAttempts);
                var result = attempt.IsExceptionFailure
                    ? WorkExecutionResult.Failure([attempt.RequiredExceptionFailureMessage])
                    : attempt.RequiredResult;
                worker.CompleteRetryIteration(result, delay, retryAttempts);
                workerEvents.IterationFailed(worker);
                if (attempt.IsExceptionFailure)
                {
                    attemptRunner.LogRetrying(worker, attempt, retryAttempts, transientRetry.Count, delay);
                }
                workerEvents.Retrying(worker, delay);
                await worker.WaitForRecurrenceInterval(delay, cancellationToken);

                if (!worker.TryBeginRetryIteration())
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

    internal static TimeSpan GetRetryDelay(WorkTransientRetryConfiguration transientRetry, int retryAttempt)
    {
        var baseDelay = transientRetry.Backoff switch
        {
            WorkRetryBackoff.Exponential => MultiplyAndCap(transientRetry.InitialDelay, retryAttempt, transientRetry.MaximumDelay),
            _ => transientRetry.InitialDelay,
        };

        if (transientRetry.Jitter <= TimeSpan.Zero)
        {
            return baseDelay;
        }

        var jitterTicks = Random.Shared.NextInt64(transientRetry.Jitter.Ticks + 1);
        var delayTicks = baseDelay.Ticks > TimeSpan.MaxValue.Ticks - jitterTicks
            ? TimeSpan.MaxValue.Ticks
            : baseDelay.Ticks + jitterTicks;

        return TimeSpan.FromTicks(delayTicks);
    }

    private static TimeSpan MultiplyAndCap(TimeSpan initialDelay, int retryAttempt, TimeSpan maximumDelay)
    {
        var multiplier = 1L << Math.Min(retryAttempt - 1, 62);
        var ticks = initialDelay.Ticks > maximumDelay.Ticks / multiplier
            ? maximumDelay.Ticks
            : initialDelay.Ticks * multiplier;

        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }
}
