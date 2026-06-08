using Microsoft.Extensions.Logging;

namespace Workable;
/// <summary>
/// Configures execution behavior for one work definition registration.
/// </summary>
public interface IWorkConfigurationBuilder
{
    /// <summary>
    /// Replaces the work start behavior.
    /// </summary>
    /// <param name="start">The start policy configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseStart(WorkStartConfiguration start);

    /// <summary>
    /// Replaces the work coordination configuration, including persistence, concurrency, idempotency, and durability settings.
    /// </summary>
    /// <param name="coordination">The coordination configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseCoordination(WorkCoordinationConfiguration coordination);

    /// <summary>
    /// Replaces the recurrence configuration.
    /// </summary>
    /// <param name="recurrence">The recurrence configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseRecurrence(WorkRecurrenceConfiguration recurrence);

    /// <summary>
    /// Replaces the transient retry configuration.
    /// </summary>
    /// <param name="transientRetry">The transient retry configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseTransientRetry(WorkTransientRetryConfiguration transientRetry);

    /// <summary>
    /// Replaces the retained logging configuration.
    /// </summary>
    /// <param name="logging">The logging configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseLogging(WorkLoggingConfiguration logging);

    /// <summary>
    /// Replaces the worker retention configuration.
    /// </summary>
    /// <param name="retention">The retention configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseRetention(WorkRetentionConfiguration retention);

    /// <summary>
    /// Replaces the invocation-channel configuration.
    /// </summary>
    /// <param name="invocation">The invocation configuration to apply to the registration.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder UseInvocation(WorkInvocationConfiguration invocation);

    /// <summary>
    /// Enables persistent coordination storage for the work definition.
    /// </summary>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder CoordinatePersistently();

    /// <summary>
    /// Sets a simple fixed recurrence interval.
    /// </summary>
    /// <param name="interval">The time between recurring executions.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder RecurEvery(TimeSpan interval);

    /// <summary>
    /// Disables recurrence for the definition.
    /// </summary>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder DisableRecurrence();

    /// <summary>
    /// Configures transient retry behavior with a retry count and backoff settings.
    /// </summary>
    /// <param name="count">The number of retry attempts after the initial failure.</param>
    /// <param name="initialDelay">The delay before the first retry attempt.</param>
    /// <param name="jitter">Optional randomization applied to retry delays.</param>
    /// <param name="maximumDelay">Optional upper bound for the computed retry delay.</param>
    /// <param name="backoff">The backoff strategy used to expand the delay across retries.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder RetryTransientFailures(
        int count,
        TimeSpan initialDelay,
        TimeSpan? jitter = null,
        TimeSpan? maximumDelay = null,
        WorkRetryBackoff backoff = WorkRetryBackoff.Exponential);

    /// <summary>
    /// Configures whether retained worker logging is enabled and how much data Workable keeps.
    /// </summary>
    /// <param name="isEnabled">Whether retained worker logging should be enabled.</param>
    /// <param name="level">The minimum log level Workable should retain for the worker.</param>
    /// <param name="maximumBufferedEntries">The maximum number of retained log entries per worker.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder ConfigureLogging(
        bool isEnabled = true,
        LogLevel level = LogLevel.Information,
        int maximumBufferedEntries = 100);

    /// <summary>
    /// Configures retained-worker cleanup behavior.
    /// </summary>
    /// <param name="purgeInterval">The interval between background retention sweeps.</param>
    /// <param name="maximumFinalWorkers">The maximum number of final-state workers to retain for the definition.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder ConfigureRetention(
        TimeSpan? purgeInterval = null,
        int? maximumFinalWorkers = null);

    /// <summary>
    /// Limits the number of workers that may run or block each other under the definition's concurrency policy.
    /// </summary>
    /// <param name="maximumCapacity">The maximum number of workers permitted by the concurrency gate.</param>
    /// <param name="scope">The scope at which Workable should apply the concurrency limit.</param>
    /// <param name="blockingMode">The worker states that count toward the concurrency limit.</param>
    /// <param name="limitReachedBehavior">The behavior to apply when a queue request exceeds the limit.</param>
    /// <param name="overrideBehavior">How Workable should treat explicit queue-time concurrency overrides.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder LimitConcurrency(
        int maximumCapacity,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerDefinition,
        WorkConcurrencyBlockingMode blockingMode = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior = WorkConcurrencyLimitReachedBehavior.Ignore,
        WorkConcurrencyOverrideBehavior overrideBehavior = WorkConcurrencyOverrideBehavior.Flexible);

    /// <summary>
    /// Enables durable queue coordination and optionally configures the fallback polling interval.
    /// </summary>
    /// <param name="fallbackPollingInterval">
    /// The polling interval to use when a push-based durable completion notification is unavailable.
    /// </param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder QueueDurably(TimeSpan? fallbackPollingInterval = null);

    /// <summary>
    /// Requires completion state to be persisted durably before a worker is treated as finished.
    /// </summary>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder CompleteDurably();

    /// <summary>
    /// Sets the start policy so queue requests are accepted without attempting to start work immediately.
    /// </summary>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder DoNotStart();

    /// <summary>
    /// Sets the start policy so queue requests return after the worker has started.
    /// </summary>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder ReturnAfterStarted();

    /// <summary>
    /// Sets the start policy so queue requests wait for worker completion before returning.
    /// </summary>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder ReturnAfterCompleted();

    /// <summary>
    /// Queues the work automatically when the system starts.
    /// </summary>
    /// <param name="instanceCount">The number of startup workers to queue for the definition.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder WithAutomaticStart(int instanceCount = 1);

    /// <summary>
    /// Queues the work automatically when the system starts and supplies typed input for each startup worker.
    /// </summary>
    /// <typeparam name="TInput">The logical input type to serialize into the startup queue request.</typeparam>
    /// <param name="inputFactory">A factory Workable calls during startup to create the input payload.</param>
    /// <param name="instanceCount">The number of startup workers to queue for the definition.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder WithAutomaticStart<TInput>(
        Func<TInput> inputFactory,
        int instanceCount = 1);

    /// <summary>
    /// Adds an initializer that Workable runs before the executor.
    /// </summary>
    /// <typeparam name="TInitializer">The initializer type that Workable should resolve from DI.</typeparam>
    /// <param name="timing">When Workable should run the initializer relative to worker execution.</param>
    /// <param name="executionOrder">An optional ordering value relative to other initializers on the same definition.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder WithInitialization<TInitializer>(
        WorkInitializationTiming timing = WorkInitializationTiming.OncePerWorker,
        int? executionOrder = null)
        where TInitializer : class;

    /// <summary>
    /// Enables subject-based idempotency and configures how Workable handles duplicate subjects.
    /// </summary>
    /// <param name="conflictPolicy">The action to take when a duplicate subject is queued.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder RejectDuplicateSubjects(
        WorkIdempotencyConflictPolicy conflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates);

    /// <summary>
    /// Adds allowed invocation channels to the definition.
    /// </summary>
    /// <param name="channels">The invocation channels that callers may use for the definition.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder AllowInvocationFrom(params WorkInvocationChannel[] channels);

    /// <summary>
    /// Adds an exception classifier that applies only to the current work definition registration.
    /// </summary>
    /// <param name="classifier">The classifier Workable evaluates when this work throws.</param>
    /// <returns>The same builder so additional configuration can be chained.</returns>
    IWorkConfigurationBuilder ClassifyExceptions(WorkExceptionClassifier classifier);
}
