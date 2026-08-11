namespace Workable;

/// <summary>
/// Aggregates the complete execution configuration for a work definition or worker instance.
/// </summary>
/// <remarks>
/// The same shape is used for attribute defaults, fluent registration, queue-time worker overrides, and runtime
/// reconfiguration. Invocation settings are definition-scoped and therefore excluded from runtime option merges.
/// </remarks>
/// <param name="Start">The start and queue-wait behavior.</param>
/// <param name="Coordination">The coordination features for idempotency, concurrency, and durability.</param>
/// <param name="Recurrence">The recurrence behavior used after each iteration completes.</param>
/// <param name="TransientRetry">The transient retry strategy for retryable failures.</param>
/// <param name="FailedWorker">The policy used when a worker settles into the failed state.</param>
/// <param name="Logging">The worker-scoped log capture settings.</param>
/// <param name="Retention">The final-worker retention settings.</param>
/// <param name="Invocation">The allowed invocation channels for the work definition.</param>
public sealed record WorkConfiguration(
    WorkStartConfiguration Start,
    WorkCoordinationConfiguration Coordination,
    WorkRecurrenceConfiguration Recurrence,
    WorkTransientRetryConfiguration TransientRetry,
    WorkFailedWorkerConfiguration FailedWorker,
    WorkLoggingConfiguration Logging,
    WorkRetentionConfiguration Retention,
    WorkInvocationConfiguration Invocation)
{
    /// <summary>
    /// Gets the definition-scoped allowlist for delegated child execution.
    /// </summary>
    public WorkChildExecutionConfiguration ChildExecution { get; init; } =
        WorkChildExecutionConfiguration.Default;

    /// <summary>
    /// Gets the persistent execution-diagnostics policy for this work definition.
    /// </summary>
    public WorkExecutionDiagnosticsPersistenceConfiguration ExecutionDiagnostics { get; init; } =
        WorkExecutionDiagnosticsPersistenceConfiguration.Default;

    /// <summary>
    /// Gets the default configuration used when no explicit work configuration is supplied.
    /// </summary>
    public static WorkConfiguration Default { get; } = new(
        WorkStartConfiguration.Default,
        WorkCoordinationConfiguration.Default,
        WorkRecurrenceConfiguration.Default,
        WorkTransientRetryConfiguration.Default,
        WorkFailedWorkerConfiguration.Default,
        WorkLoggingConfiguration.Default,
        WorkRetentionConfiguration.Default,
        WorkInvocationConfiguration.Default);

    /// <summary>
    /// Merges runtime worker overrides into the current configuration.
    /// </summary>
    /// <param name="overrides">The queue-time or reconfiguration override values to apply.</param>
    /// <returns>
    /// A new configuration instance with runtime-overridable facets replaced by <paramref name="overrides"/>.
    /// </returns>
    public WorkConfiguration MergeRuntimeOptions(WorkConfiguration? overrides)
        => overrides is null
            ? this
            : this with
            {
                Start = overrides.Start,
                Coordination = overrides.Coordination,
                Recurrence = overrides.Recurrence,
                TransientRetry = overrides.TransientRetry,
                FailedWorker = overrides.FailedWorker,
                Logging = overrides.Logging,
                Retention = overrides.Retention,
                // Persistent execution diagnostics are definition-scoped. Queue-time and worker-level
                // overrides must not enable or disable capture for an individual worker.
                // Invocation is intentionally excluded. Allowed invocation channels are a
                // design-time contract for the work definition, not a runtime worker option.
                // Child execution is also definition-scoped and cannot be changed per worker.
            };

    /// <summary>
    /// Merges override values into the current configuration.
    /// </summary>
    /// <param name="overrides">The override values to apply.</param>
    /// <returns>The merged configuration.</returns>
    public WorkConfiguration Merge(WorkConfiguration? overrides)
        => this.MergeRuntimeOptions(overrides);
}
