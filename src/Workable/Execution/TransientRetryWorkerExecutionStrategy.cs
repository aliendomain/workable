namespace Workable;

internal sealed class TransientRetryWorkerExecutionStrategy(
    WorkerExecutionAttemptRunner attemptRunner,
    WorkerExecutionCompletionRecorder completionRecorder) : IWorkerExecutionStrategy
{
    public async Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        try
        {
            var attempt = await attemptRunner.Execute(worker, allowTransientRetry: true, cancellationToken);
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
