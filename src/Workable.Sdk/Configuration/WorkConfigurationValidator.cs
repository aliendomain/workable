namespace Workable;
internal static class WorkConfigurationValidator
{
    public static WorkConfiguration ThrowIfInvalid(WorkConfiguration configuration)
    {
        var messages = Validate(configuration);
        if (messages.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, messages.Select(message => message.Text)));
        }

        return configuration;
    }

    public static IReadOnlyList<WorkMessage> Validate(WorkConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var messages = new List<WorkMessage>();
        ValidateRecurrence(configuration.Recurrence, messages);
        ValidateTransientRetry(configuration.TransientRetry, messages);
        ValidateFailedWorker(configuration.FailedWorker, configuration.Recurrence, messages);
        ValidateLogging(configuration.Logging, messages);
        ValidateRetention(configuration.Retention, messages);
        ValidateCoordination(configuration.Coordination, configuration.Recurrence, messages);
        ValidateInvocation(configuration.Invocation, messages);
        return messages;
    }

    public static IReadOnlyList<WorkMessage> ValidatePersistenceStore(
        WorkConfiguration configuration,
        bool persistenceStoreAvailable)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (persistenceStoreAvailable || !configuration.Coordination.RequiresPersistenceStore)
        {
            return [];
        }

        return
        [
            WorkMessage.Error(
                "workable.configuration.coordination.persistence_store_required",
                "Persistent coordination requires a registered Workable persistence store.",
                "configuration.coordination.storage"),
        ];
    }

    public static IReadOnlyList<WorkMessage> ValidateConcurrencyInput(
        WorkCoordinationConfiguration coordination,
        WorkInput? input)
    {
        if (!coordination.IsConcurrencyEnabled)
        {
            return [];
        }

        return coordination.Concurrency.Scope switch
        {
            WorkConcurrencyScope.PerSubject when input?.SubjectId is null =>
                [ConcurrencyInputError(
                    "workable.concurrency.subject_required",
                    "Concurrency scoped by subject requires a work subject id.",
                    "input.subjectId")],
            WorkConcurrencyScope.PerConcurrencyKey when input?.ConcurrencyKey is null =>
                [ConcurrencyInputError(
                    "workable.concurrency.key_required",
                    "Concurrency scoped by concurrency key requires a work concurrency key.",
                    "input.concurrencyKey")],
            _ => [],
        };
    }

    private static void ValidateRecurrence(WorkRecurrenceConfiguration recurrence, List<WorkMessage> messages)
    {
        if (recurrence.IsEnabled && recurrence.Interval <= TimeSpan.Zero)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.recurrence.interval_required",
                "Recurring work requires a recurrence interval greater than zero.",
                "configuration.recurrence.interval"));
        }

        if (recurrence.CircuitBreakerFailureThreshold <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.recurrence.circuit_breaker_failure_threshold_required",
                "Recurrence circuit breaker failure threshold must be greater than zero.",
                "configuration.recurrence.circuitBreakerFailureThreshold"));
        }

        if (recurrence.RetainedIterations <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.recurrence.retained_iterations_required",
                "Recurrence retained iterations must be greater than zero.",
                "configuration.recurrence.retainedIterations"));
        }
    }

    private static void ValidateTransientRetry(WorkTransientRetryConfiguration transientRetry, List<WorkMessage> messages)
    {
        if (transientRetry.Count < 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.transient_retry.count_negative",
                "Transient retry count cannot be negative.",
                "configuration.transientRetry.count"));
        }

        if (transientRetry.Count > 0 && transientRetry.InitialDelay <= TimeSpan.Zero)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.transient_retry.initial_delay_required",
                "Transient retry initial delay must be greater than zero when transient retries are enabled.",
                "configuration.transientRetry.initialDelay"));
        }

        if (transientRetry.Jitter < TimeSpan.Zero)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.transient_retry.jitter_negative",
                "Transient retry jitter cannot be negative.",
                "configuration.transientRetry.jitter"));
        }

        if (transientRetry.MaximumDelay <= TimeSpan.Zero)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.transient_retry.maximum_delay_required",
                "Transient retry maximum delay must be greater than zero.",
                "configuration.transientRetry.maximumDelay"));
        }

        if (transientRetry.InitialDelay > transientRetry.MaximumDelay)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.transient_retry.initial_delay_exceeds_maximum",
                "Transient retry initial delay cannot be greater than the maximum retry delay.",
                "configuration.transientRetry.initialDelay"));
        }
    }

    private static void ValidateFailedWorker(
        WorkFailedWorkerConfiguration failedWorker,
        WorkRecurrenceConfiguration recurrence,
        List<WorkMessage> messages)
    {
        if (failedWorker.AutoCancelAfter <= TimeSpan.Zero)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.failed_worker.auto_cancel_after_required",
                "Failed-worker auto-cancel delay must be greater than zero.",
                "configuration.failedWorker.autoCancelAfter"));
        }

        if (recurrence.IsEnabled && failedWorker.Handling == WorkFailedWorkerHandling.AutoCancel)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.failed_worker.auto_cancel_recurring_not_supported",
                "Failed-worker auto-cancel is not supported for recurring work.",
                "configuration.failedWorker.handling"));
        }
    }

    private static void ValidateRetention(WorkRetentionConfiguration retention, List<WorkMessage> messages)
    {
        if (retention.PurgeInterval <= TimeSpan.Zero)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.retention.purge_interval_required",
                "Retention purge interval must be greater than zero.",
                "configuration.retention.purgeInterval"));
        }

        if (retention.MaximumFinalWorkers <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.retention.maximum_final_workers_required",
                "Retention maximum final workers must be greater than zero.",
                "configuration.retention.maximumFinalWorkers"));
        }
    }

    private static void ValidateLogging(WorkLoggingConfiguration logging, List<WorkMessage> messages)
    {
        if (logging.MaximumBufferedEntries < 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.logging.maximum_buffered_entries_negative",
                "Logging maximum buffered entries cannot be negative.",
                "configuration.logging.maximumBufferedEntries"));
        }
    }

    private static void ValidateCoordination(
        WorkCoordinationConfiguration coordination,
        WorkRecurrenceConfiguration recurrence,
        List<WorkMessage> messages)
    {
        if (!coordination.IsEnabled)
        {
            if (coordination.Idempotency.IsEnabled ||
                coordination.Concurrency.IsEnabled ||
                coordination.Durability.IsEnabled ||
                coordination.Durability.CompleteDurably)
            {
                messages.Add(WorkMessage.Error(
                    "workable.configuration.coordination.disabled_with_features",
                    "Coordination must be enabled before idempotency, concurrency, or durability can be enabled.",
                    "configuration.coordination.isEnabled"));
            }

            ValidateConcurrency(coordination, messages);
            return;
        }

        ValidateConcurrency(coordination, messages);
        ValidateDurability(coordination, recurrence, messages);
    }

    private static void ValidateConcurrency(
        WorkCoordinationConfiguration coordination,
        List<WorkMessage> messages)
    {
        var concurrency = coordination.Concurrency;
        if (concurrency.MaximumCapacity < 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.maximum_capacity_negative",
                "Concurrency maximum capacity cannot be negative.",
                "configuration.coordination.concurrency.maximumCapacity"));
        }

        if (!coordination.IsConcurrencyEnabled)
        {
            return;
        }

        if (concurrency.MaximumCapacity <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.maximum_capacity_required",
                "Enabled concurrency requires maximum capacity greater than zero.",
                "configuration.coordination.concurrency.maximumCapacity"));
        }

        if (coordination.Storage != WorkCoordinationStorage.Persistent)
        {
            return;
        }

        if (!coordination.Durability.IsEnabled)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.persistence_requires_durable_queue",
                "Persistence-backed concurrency currently requires durable queueing.",
                "configuration.coordination.durability.isEnabled"));
        }

        if (concurrency.BlockingMode != WorkConcurrencyBlockingMode.WhileExecuting)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.persistence_blocking_mode_not_supported",
                "Persistence-backed concurrency currently supports only WhileExecuting blocking mode.",
                "configuration.coordination.concurrency.blockingMode"));
        }

        if (concurrency.LimitReachedBehavior != WorkConcurrencyLimitReachedBehavior.DeferStart)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.persistence_requires_deferred_start",
                "Persistence-backed concurrency currently requires DeferStart limit behavior.",
                "configuration.coordination.concurrency.limitReachedBehavior"));
        }

    }

    private static void ValidateDurability(
        WorkCoordinationConfiguration coordination,
        WorkRecurrenceConfiguration recurrence,
        List<WorkMessage> messages)
    {
        var durability = coordination.Durability;
        if (coordination.Storage != WorkCoordinationStorage.Persistent)
        {
            if (durability.IsEnabled || durability.CompleteDurably)
            {
                messages.Add(WorkMessage.Error(
                    "workable.configuration.coordination.durability_requires_persistent_storage",
                    "Durability requires persistent coordination storage.",
                    "configuration.coordination.storage"));
            }

            return;
        }

        if (durability.CompleteDurably &&
            !durability.IsEnabled &&
            !coordination.Idempotency.IsEnabled)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.queue_durability.durable_completion_requires_persistence",
                "Durable completion requires durable queueing or persistence-backed idempotency.",
                "configuration.coordination.durability.completeDurably"));
        }

        if (durability.IsEnabled &&
            durability.FallbackPollingInterval < WorkQueueDurabilityConfiguration.MinimumFallbackPollingInterval)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.queue_durability.fallback_polling_interval_too_short",
                "Durable queue fallback polling interval must be at least one second.",
                "configuration.coordination.durability.fallbackPollingInterval"));
        }

        if (durability.CompleteDurably && recurrence.IsEnabled)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.queue_durability.durable_completion_recurring_not_supported",
                "Durable completion is not supported for recurring work.",
                "configuration.coordination.durability.completeDurably"));
        }
    }

    private static void ValidateInvocation(WorkInvocationConfiguration invocation, List<WorkMessage> messages)
    {
        if (invocation.AllowedChannels.Count == 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.invocation.channels_required",
                "Invocation configuration requires at least one allowed channel.",
                "configuration.invocation.allowedChannels"));
        }
    }

    private static WorkMessage ConcurrencyInputError(
        string code,
        string message,
        string path)
        => WorkMessage.Error(code, message, path);
}
