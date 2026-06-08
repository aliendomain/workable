using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkConfigurationBuilder(WorkConfiguration configuration) : IWorkConfigurationBuilder
{
    private readonly List<WorkExceptionClassifier> exceptionClassifiers = [];
    private readonly List<WorkAutomaticStartRegistration> automaticStarts = [];
    private readonly List<WorkInitializationRegistration> initializers = [];
    private WorkConfiguration configuration = configuration;

    public IWorkConfigurationBuilder UseStart(WorkStartConfiguration start)
    {
        ArgumentNullException.ThrowIfNull(start);

        this.configuration = this.configuration with
        {
            Start = start,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseCoordination(WorkCoordinationConfiguration coordination)
    {
        ArgumentNullException.ThrowIfNull(coordination);

        this.configuration = this.configuration with
        {
            Coordination = coordination,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseRecurrence(WorkRecurrenceConfiguration recurrence)
    {
        ArgumentNullException.ThrowIfNull(recurrence);

        this.configuration = this.configuration with
        {
            Recurrence = recurrence,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseTransientRetry(WorkTransientRetryConfiguration transientRetry)
    {
        ArgumentNullException.ThrowIfNull(transientRetry);

        this.configuration = this.configuration with
        {
            TransientRetry = transientRetry,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseFailedWorker(WorkFailedWorkerConfiguration failedWorker)
    {
        ArgumentNullException.ThrowIfNull(failedWorker);

        this.configuration = this.configuration with
        {
            FailedWorker = failedWorker,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseLogging(WorkLoggingConfiguration logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        this.configuration = this.configuration with
        {
            Logging = logging,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseRetention(WorkRetentionConfiguration retention)
    {
        ArgumentNullException.ThrowIfNull(retention);

        this.configuration = this.configuration with
        {
            Retention = retention,
        };
        return this;
    }

    public IWorkConfigurationBuilder UseInvocation(WorkInvocationConfiguration invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        this.configuration = this.configuration with
        {
            Invocation = invocation,
        };
        return this;
    }

    public IWorkConfigurationBuilder CoordinatePersistently()
    {
        this.configuration = this.configuration with
        {
            Coordination = this.configuration.Coordination with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder QueueDurably(TimeSpan? fallbackPollingInterval = null)
    {
        this.configuration = this.configuration with
        {
            Coordination = this.configuration.Coordination with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                    CompleteDurably = this.configuration.Coordination.Durability.CompleteDurably,
                    FallbackPollingInterval = fallbackPollingInterval ?? WorkQueueDurabilityConfiguration.DefaultFallbackPollingInterval,
                },
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder CompleteDurably()
    {
        this.configuration = this.configuration with
        {
            Coordination = this.configuration.Coordination with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = this.configuration.Coordination.Durability with
                {
                    CompleteDurably = true,
                },
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder RecurEvery(TimeSpan interval)
    {
        this.configuration = this.configuration with
        {
            Recurrence = WorkRecurrenceConfiguration.Every(interval),
        };
        return this;
    }

    public IWorkConfigurationBuilder RetryTransientFailures(
        int count,
        TimeSpan initialDelay,
        TimeSpan? jitter = null,
        TimeSpan? maximumDelay = null,
        WorkRetryBackoff backoff = WorkRetryBackoff.Exponential)
    {
        this.configuration = this.configuration with
        {
            TransientRetry = this.configuration.TransientRetry with
            {
                Count = count,
                InitialDelay = initialDelay,
                Jitter = jitter ?? WorkTransientRetryConfiguration.Default.Jitter,
                MaximumDelay = maximumDelay ?? WorkTransientRetryConfiguration.Default.MaximumDelay,
                Backoff = backoff,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder ConfigureFailedWorker(
        WorkFailedWorkerHandling? handling = null,
        TimeSpan? autoCancelAfter = null)
    {
        this.configuration = this.configuration with
        {
            FailedWorker = this.configuration.FailedWorker with
            {
                Handling = handling ?? this.configuration.FailedWorker.Handling,
                AutoCancelAfter = autoCancelAfter ?? this.configuration.FailedWorker.AutoCancelAfter,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder AutoCancelFailedWorkersAfter(TimeSpan autoCancelAfter)
        => this.ConfigureFailedWorker(WorkFailedWorkerHandling.AutoCancel, autoCancelAfter);

    public IWorkConfigurationBuilder ConfigureLogging(
        bool isEnabled = true,
        LogLevel level = LogLevel.Information,
        int maximumBufferedEntries = 100)
    {
        this.configuration = this.configuration with
        {
            Logging = this.configuration.Logging with
            {
                IsEnabled = isEnabled,
                Level = level,
                MaximumBufferedEntries = maximumBufferedEntries,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder ConfigureRetention(
        TimeSpan? purgeInterval = null,
        int? maximumFinalWorkers = null)
    {
        this.configuration = this.configuration with
        {
            Retention = this.configuration.Retention with
            {
                PurgeInterval = purgeInterval ?? WorkRetentionConfiguration.Default.PurgeInterval,
                MaximumFinalWorkers = maximumFinalWorkers ?? WorkRetentionConfiguration.Default.MaximumFinalWorkers,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder LimitConcurrency(
        int maximumCapacity,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerDefinition,
        WorkConcurrencyBlockingMode blockingMode = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior = WorkConcurrencyLimitReachedBehavior.Ignore,
        WorkConcurrencyOverrideBehavior overrideBehavior = WorkConcurrencyOverrideBehavior.Flexible)
    {
        this.configuration = this.configuration with
        {
            Coordination = this.configuration.Coordination with
            {
                IsEnabled = true,
                Concurrency = this.configuration.Coordination.Concurrency with
                {
                    IsEnabled = true,
                    MaximumCapacity = maximumCapacity,
                    Scope = scope,
                    BlockingMode = blockingMode,
                    LimitReachedBehavior = limitReachedBehavior,
                    OverrideBehavior = overrideBehavior,
                },
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder DoNotStart()
    {
        this.configuration = this.configuration with
        {
            Start = WorkStartConfiguration.DoNotStart,
        };
        return this;
    }

    public IWorkConfigurationBuilder ReturnAfterStarted()
    {
        this.configuration = this.configuration with
        {
            Start = new WorkStartConfiguration
            {
                Policy = WorkStartPolicy.StartAndReturnAfterStarted,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder ReturnAfterCompleted()
    {
        this.configuration = this.configuration with
        {
            Start = new WorkStartConfiguration
            {
                Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder RejectDuplicateSubjects(
        WorkIdempotencyConflictPolicy conflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates)
    {
        this.configuration = this.configuration with
        {
            Coordination = this.configuration.Coordination with
            {
                IsEnabled = true,
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                    ConflictPolicy = conflictPolicy,
                },
            },
        };
        return this;
    }

    public IWorkConfigurationBuilder WithAutomaticStart(int instanceCount = 1)
    {
        this.automaticStarts.Add(WorkAutomaticStartRegistration.Create(instanceCount, _ => null));
        return this;
    }

    public IWorkConfigurationBuilder WithAutomaticStart<TInput>(
        Func<TInput> inputFactory,
        int instanceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(inputFactory);

        this.automaticStarts.Add(WorkAutomaticStartRegistration.Create(
            instanceCount,
            _ => StartupWorkInput(inputFactory())));
        return this;
    }

    public IWorkConfigurationBuilder WithInitialization<TInitializer>(
        WorkInitializationTiming timing = WorkInitializationTiming.OncePerWorker,
        int? executionOrder = null)
        where TInitializer : class
    {
        this.initializers.Add(WorkInitializationRegistration.Create<TInitializer>(timing, executionOrder));
        return this;
    }

    public IWorkConfigurationBuilder AllowInvocationFrom(params WorkInvocationChannel[] channels)
    {
        this.configuration = this.configuration with
        {
            Invocation = this.configuration.Invocation.AllowAdditional(channels),
        };
        return this;
    }

    public IWorkConfigurationBuilder ClassifyExceptions(WorkExceptionClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        this.exceptionClassifiers.Add(classifier);
        return this;
    }

    public IWorkConfigurationBuilder DisableRecurrence()
    {
        this.configuration = this.configuration with
        {
            Recurrence = WorkRecurrenceConfiguration.Disabled,
        };
        return this;
    }

    internal WorkConfiguration Build()
        => this.configuration;

    internal IReadOnlyList<WorkExceptionClassifier> BuildExceptionClassifiers()
        => [.. this.exceptionClassifiers];

    internal IReadOnlyList<WorkAutomaticStartRegistration> BuildAutomaticStarts()
        => [.. this.automaticStarts];

    internal IReadOnlyList<WorkInitializationRegistration> BuildInitializers()
        => [.. this.initializers];

    private static WorkInput? StartupWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };
}
