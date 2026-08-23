using Microsoft.Extensions.Logging;

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
        ValidateStart(configuration.Start, messages);
        ValidateRecurrence(configuration.Recurrence, messages);
        ValidateTransientRetry(configuration.TransientRetry, messages);
        ValidateFailedWorker(configuration.FailedWorker, configuration.Recurrence, messages);
        ValidateLogging(configuration.Logging, messages);
        ValidateExecutionDiagnostics(configuration.ExecutionDiagnostics, messages);
        ValidateRetention(configuration.Retention, messages);
        ValidateCoordination(configuration.Coordination, configuration.Recurrence, messages);
        ValidateInvocation(configuration.Invocation, messages);
        ValidateChildExecution(configuration.ChildExecution, messages);
        return messages;
    }

    public static IReadOnlyList<WorkMessage> ValidateWorkerOptions(
        WorkerOptions options,
        string targetPrefix = "options")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPrefix);

        return Enum.IsDefined(options.ProfilingCaptureMode)
            ? []
            :
            [
                InvalidEnum(
                    "workable.options.profiling_capture_mode.invalid",
                    $"Profiling capture mode '{options.ProfilingCaptureMode}' is not supported.",
                    $"{targetPrefix}.profilingCaptureMode"),
            ];
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

    public static IReadOnlyList<WorkMessage> ValidateExecutionDiagnosticsRepository(
        WorkConfiguration configuration,
        bool repositoryAvailable)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (repositoryAvailable || configuration.ExecutionDiagnostics.IsEnabled != true)
        {
            return [];
        }

        return
        [
            WorkMessage.Error(
                "workable.configuration.execution_diagnostics.repository_required",
                "Persistent execution diagnostics require a registered execution diagnostics repository.",
                "configuration.executionDiagnostics.isEnabled"),
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

        if (!Enum.IsDefined(coordination.Concurrency.Scope))
        {
            return
            [
                InvalidEnum(
                    "workable.configuration.concurrency.scope.invalid",
                    $"Concurrency scope '{coordination.Concurrency.Scope}' is not supported.",
                    "configuration.coordination.concurrency.scope"),
            ];
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

    private static void ValidateStart(
        WorkStartConfiguration start,
        List<WorkMessage> messages)
    {
        if (!Enum.IsDefined(start.Policy))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.start.policy.invalid",
                $"Start policy '{start.Policy}' is not supported.",
                "configuration.start.policy"));
        }
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
        if (!Enum.IsDefined(transientRetry.Backoff))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.transient_retry.backoff.invalid",
                $"Transient retry backoff '{transientRetry.Backoff}' is not supported.",
                "configuration.transientRetry.backoff"));
        }

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
        if (!Enum.IsDefined(failedWorker.Handling))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.failed_worker.handling.invalid",
                $"Failed-worker handling '{failedWorker.Handling}' is not supported.",
                "configuration.failedWorker.handling"));
        }

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
        if (!Enum.IsDefined(logging.Level))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.logging.level.invalid",
                $"Logging level '{logging.Level}' is not supported.",
                "configuration.logging.level"));
        }

        if (logging.MaximumBufferedEntries < 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.logging.maximum_buffered_entries_negative",
                "Logging maximum buffered entries cannot be negative.",
                "configuration.logging.maximumBufferedEntries"));
        }
    }

    private static void ValidateExecutionDiagnostics(
        WorkExecutionDiagnosticsPersistenceConfiguration persistence,
        List<WorkMessage> messages)
    {
        if (persistence.Retention < WorkExecutionDiagnosticsPersistenceConfiguration.MinimumRetention ||
            persistence.Retention > WorkExecutionDiagnosticsPersistenceConfiguration.MaximumRetention)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.execution_diagnostics.retention_out_of_range",
                "Execution diagnostics retention must be between one minute and 30 days.",
                "configuration.executionDiagnostics.retention"));
        }

        if (!Enum.IsDefined(persistence.MinimumLogLevel))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.execution_diagnostics.log_level_invalid",
                $"Execution diagnostics log level '{persistence.MinimumLogLevel}' is not supported.",
                "configuration.executionDiagnostics.minimumLogLevel"));
        }
        else if (persistence.IsEnabled == true && persistence.MinimumLogLevel == LogLevel.None)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.execution_diagnostics.log_level_required",
                "Enabled execution diagnostics require a persistent log level other than None.",
                "configuration.executionDiagnostics.minimumLogLevel"));
        }

        if (!Enum.IsDefined(persistence.ProfileCaptureMode))
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.execution_diagnostics.profile_capture_mode_invalid",
                $"Execution diagnostics profile capture mode '{persistence.ProfileCaptureMode}' is not supported.",
                "configuration.executionDiagnostics.profileCaptureMode"));
        }
    }

    private static void ValidateCoordination(
        WorkCoordinationConfiguration coordination,
        WorkRecurrenceConfiguration recurrence,
        List<WorkMessage> messages)
    {
        if (!Enum.IsDefined(coordination.Storage))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.coordination.storage.invalid",
                $"Coordination storage '{coordination.Storage}' is not supported.",
                "configuration.coordination.storage"));
        }

        if (!Enum.IsDefined(coordination.Idempotency.ConflictPolicy))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.idempotency.conflict_policy.invalid",
                $"Idempotency conflict policy '{coordination.Idempotency.ConflictPolicy}' is not supported.",
                "configuration.coordination.idempotency.conflictPolicy"));
        }

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
        if (!Enum.IsDefined(concurrency.Scope))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.concurrency.scope.invalid",
                $"Concurrency scope '{concurrency.Scope}' is not supported.",
                "configuration.coordination.concurrency.scope"));
        }

        if (!Enum.IsDefined(concurrency.BlockingMode))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.concurrency.blocking_mode.invalid",
                $"Concurrency blocking mode '{concurrency.BlockingMode}' is not supported.",
                "configuration.coordination.concurrency.blockingMode"));
        }

        if (!Enum.IsDefined(concurrency.LimitReachedBehavior))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.concurrency.limit_reached_behavior.invalid",
                $"Concurrency limit-reached behavior '{concurrency.LimitReachedBehavior}' is not supported.",
                "configuration.coordination.concurrency.limitReachedBehavior"));
        }

        if (!Enum.IsDefined(concurrency.OverrideBehavior))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.concurrency.override_behavior.invalid",
                $"Concurrency override behavior '{concurrency.OverrideBehavior}' is not supported.",
                "configuration.coordination.concurrency.overrideBehavior"));
        }

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

        foreach (var channel in invocation.AllowedChannels.Where(channel => !Enum.IsDefined(channel)))
        {
            messages.Add(InvalidEnum(
                "workable.configuration.invocation.channel.invalid",
                $"Invocation channel '{channel}' is not supported.",
                "configuration.invocation.allowedChannels"));
        }
    }

    private static void ValidateChildExecution(
        WorkChildExecutionConfiguration childExecution,
        List<WorkMessage> messages)
    {
        if (childExecution.AllowedDefinitionNames.Any(string.IsNullOrWhiteSpace))
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.child_execution.definition_name_required",
                "Child execution configuration cannot contain an empty definition name.",
                "configuration.childExecution.allowedDefinitionNames"));
        }
    }

    private static WorkMessage ConcurrencyInputError(
        string code,
        string message,
        string path)
        => WorkMessage.Error(code, message, path);

    private static WorkMessage InvalidEnum(
        string code,
        string message,
        string target)
        => WorkMessage.Error(code, message, target);
}
