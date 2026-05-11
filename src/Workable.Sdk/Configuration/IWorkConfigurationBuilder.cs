using Microsoft.Extensions.Logging;

namespace Workable;
public interface IWorkConfigurationBuilder
{
    IWorkConfigurationBuilder UseStart(WorkStartConfiguration start);

    IWorkConfigurationBuilder UseIdempotency(WorkIdempotencyConfiguration idempotency);

    IWorkConfigurationBuilder UseRecurrence(WorkRecurrenceConfiguration recurrence);

    IWorkConfigurationBuilder UseTransientRetry(WorkTransientRetryConfiguration transientRetry);

    IWorkConfigurationBuilder UseLogging(WorkLoggingConfiguration logging);

    IWorkConfigurationBuilder UseRetention(WorkRetentionConfiguration retention);

    IWorkConfigurationBuilder UseConcurrency(WorkConcurrencyConfiguration concurrency);

    IWorkConfigurationBuilder UseInvocation(WorkInvocationConfiguration invocation);

    IWorkConfigurationBuilder RecurEvery(TimeSpan interval);

    IWorkConfigurationBuilder DisableRecurrence();

    IWorkConfigurationBuilder RetryTransientFailures(
        int count,
        TimeSpan initialDelay,
        TimeSpan? jitter = null,
        TimeSpan? maximumDelay = null,
        WorkRetryBackoff backoff = WorkRetryBackoff.Exponential);

    IWorkConfigurationBuilder ConfigureLogging(
        bool isEnabled = true,
        LogLevel level = LogLevel.Information,
        int maximumBufferedEntries = 100);

    IWorkConfigurationBuilder ConfigureRetention(TimeSpan? purgeInterval = null);

    IWorkConfigurationBuilder LimitConcurrency(
        int maximumCapacity,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerDefinition,
        WorkConcurrencyBlockingMode blockingMode = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior = WorkConcurrencyLimitReachedBehavior.Ignore,
        WorkConcurrencyOverrideBehavior overrideBehavior = WorkConcurrencyOverrideBehavior.Flexible);

    IWorkConfigurationBuilder DoNotStart();

    IWorkConfigurationBuilder ReturnAfterStarted();

    IWorkConfigurationBuilder ReturnAfterCompleted();

    IWorkConfigurationBuilder WithAutomaticStart(int instanceCount = 1);

    IWorkConfigurationBuilder WithAutomaticStart<TInput>(
        Func<TInput> inputFactory,
        int instanceCount = 1);

    IWorkConfigurationBuilder WithInitialization<TInitializer>(
        WorkInitializationTiming timing = WorkInitializationTiming.OncePerWorker,
        int? executionOrder = null)
        where TInitializer : class;

    IWorkConfigurationBuilder RejectDuplicateSubjects(
        WorkIdempotencyConflictPolicy conflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates);

    IWorkConfigurationBuilder AllowInvocationFrom(params WorkInvocationChannel[] channels);

    IWorkConfigurationBuilder ClassifyExceptions(WorkExceptionClassifier classifier);
}
