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
        ValidateLogging(configuration.Logging, messages);
        ValidateRetention(configuration.Retention, messages);
        ValidateConcurrency(configuration.Concurrency, messages);
        ValidateInvocation(configuration.Invocation, messages);
        return messages;
    }

    public static IReadOnlyList<WorkMessage> ValidateConcurrencyInput(
        WorkConcurrencyConfiguration concurrency,
        WorkInput? input)
    {
        if (!concurrency.IsEnabled)
        {
            return [];
        }

        return concurrency.Scope switch
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

        if (recurrence.RetainedSuccessfulIterations <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.recurrence.retained_successful_iterations_required",
                "Recurrence retained successful iterations must be greater than zero.",
                "configuration.recurrence.retainedSuccessfulIterations"));
        }

        if (recurrence.RetainedFailedIterations <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.recurrence.retained_failed_iterations_required",
                "Recurrence retained failed iterations must be greater than zero.",
                "configuration.recurrence.retainedFailedIterations"));
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

    private static void ValidateConcurrency(WorkConcurrencyConfiguration concurrency, List<WorkMessage> messages)
    {
        if (concurrency.MaximumCapacity < 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.maximum_capacity_negative",
                "Concurrency maximum capacity cannot be negative.",
                "configuration.concurrency.maximumCapacity"));
        }

        if (!concurrency.IsEnabled)
        {
            return;
        }

        if (concurrency.MaximumCapacity <= 0)
        {
            messages.Add(WorkMessage.Error(
                "workable.configuration.concurrency.maximum_capacity_required",
                "Enabled concurrency requires maximum capacity greater than zero.",
                "configuration.concurrency.maximumCapacity"));
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
