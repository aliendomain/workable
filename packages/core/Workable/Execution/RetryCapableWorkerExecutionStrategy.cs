namespace Workable;

internal abstract class RetryCapableWorkerExecutionStrategy(
    WorkerExecutionAttemptRunner attemptRunner,
    WorkerExecutionCompletionRecorder completionRecorder,
    WorkerEventPublisher workerEvents,
    WorkerIterationTransitionCoordinator iterationTransitions) : IWorkerExecutionStrategy
{
    protected WorkerExecutionAttemptRunner AttemptRunner { get; } = attemptRunner;

    protected WorkerExecutionCompletionRecorder CompletionRecorder { get; } = completionRecorder;

    protected WorkerEventPublisher WorkerEvents { get; } = workerEvents;

    protected WorkerIterationTransitionCoordinator IterationTransitions { get; } = iterationTransitions;

    public async Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        try
        {
            return await this.ExecuteCore(worker, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return this.CompletionRecorder.CompleteCancellation(worker);
        }
        finally
        {
            worker.DisposeExecutionResources(cancellationToken);
        }
    }

    protected abstract Task<WorkCompletion> ExecuteCore(WorkerRecord worker, CancellationToken cancellationToken);

    protected async Task<AttemptLoopResult> ExecuteAttemptLoop(WorkerRecord worker, CancellationToken cancellationToken)
    {
        var retryAttempts = 0;
        var transientRetry = worker.GetConfiguration().TransientRetry;

        while (true)
        {
            var attempt = await this.AttemptRunner.Execute(worker, retryAttempts, cancellationToken);
            if (!this.ShouldRetry(worker, attempt, retryAttempts, transientRetry))
            {
                return AttemptLoopResult.FromAttempt(attempt);
            }

            var retryTransition = await this.CompleteRetryDelayAndResume(
                worker,
                attempt,
                retryAttempts,
                transientRetry,
                cancellationToken);
            if (retryTransition.Completion is not null)
            {
                return AttemptLoopResult.FromCompletion(retryTransition.Completion);
            }

            retryAttempts = retryTransition.RetryAttempts;
        }
    }

    protected async Task<RetryIterationResult> CompleteRetryDelayAndResume(
        WorkerRecord worker,
        WorkerExecutionAttempt attempt,
        int retryAttempts,
        WorkTransientRetryConfiguration transientRetry,
        CancellationToken cancellationToken)
    {
        var nextRetryAttempt = retryAttempts + 1;
        var retryDelay = GetRetryDelay(transientRetry, nextRetryAttempt);
        var retryResult = attempt.IsExceptionFailure
            ? WorkExecutionResult.Failure([attempt.RequiredExceptionFailureMessage])
            : attempt.RequiredResult;
        worker.CompleteRetryIteration(retryResult, retryDelay, nextRetryAttempt);
        this.WorkerEvents.IterationFailed(worker);
        if (attempt.IsExceptionFailure)
        {
            this.AttemptRunner.LogRetrying(worker, attempt, nextRetryAttempt, transientRetry.Count, retryDelay);
        }

        this.WorkerEvents.Retrying(worker, retryDelay);
        await worker.WaitForRecurrenceInterval(retryDelay, cancellationToken);

        if (!this.IterationTransitions.TryBeginRetryIteration(worker))
        {
            return RetryIterationResult.Stop(worker.ToCompletion(WorkerStateMachine.CompletionStatusFor(worker.State)));
        }

        return RetryIterationResult.Continue(nextRetryAttempt);
    }

    protected static WorkExecutionResult ToFinalResult(WorkerExecutionAttempt attempt)
        => attempt.IsExceptionFailure
            ? WorkExecutionResult.Failure([attempt.RequiredExceptionFailureMessage])
            : attempt.RequiredResult;

    private bool ShouldRetry(
        WorkerRecord worker,
        WorkerExecutionAttempt attempt,
        int retryAttempts,
        WorkTransientRetryConfiguration transientRetry)
    {
        if (!attempt.IsExceptionFailure && !attempt.IsTransientDeclarativeFailure)
        {
            return false;
        }

        if (attempt.IsExceptionFailure &&
            (attempt.RequiredExceptionClassification != WorkExceptionClassification.Transient ||
            retryAttempts >= transientRetry.Count))
        {
            this.AttemptRunner.LogFinalException(worker, attempt, retryAttempts);
            return false;
        }

        return !(attempt.IsTransientDeclarativeFailure && retryAttempts >= transientRetry.Count);
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

internal readonly record struct RetryIterationResult(int RetryAttempts, WorkCompletion? Completion)
{
    public static RetryIterationResult Continue(int retryAttempts)
        => new(retryAttempts, Completion: null);

    public static RetryIterationResult Stop(WorkCompletion completion)
        => new(0, completion);
}

internal readonly record struct AttemptLoopResult(WorkerExecutionAttempt? Attempt, WorkCompletion? Completion)
{
    public WorkerExecutionAttempt RequiredAttempt
        => this.Attempt ?? throw new InvalidOperationException("Attempt loop did not produce a terminal attempt.");

    public static AttemptLoopResult FromAttempt(WorkerExecutionAttempt attempt)
        => new(attempt, Completion: null);

    public static AttemptLoopResult FromCompletion(WorkCompletion completion)
        => new(Attempt: null, completion);
}
