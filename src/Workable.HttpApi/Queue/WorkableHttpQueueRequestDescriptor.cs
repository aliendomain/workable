namespace Workable;

public sealed record WorkableHttpQueueRequestDescriptor(
    WorkSchema Schema,
    IReadOnlyList<WorkableHttpQueueRequestTab> Tabs)
{
    public static WorkableHttpQueueRequestDescriptor Create()
        => new(WorkSchema.FromType<WorkableHttpWorkRequest>(), QueueRequestTabs);

    private static readonly IReadOnlyList<WorkableHttpQueueRequestTab> QueueRequestTabs =
    [
        new(
            "queue",
            "Queue",
            "Controls how the HTTP queue call behaves and which optional identity metadata is attached to the worker input.",
            [
                Field("completion", "Completion", "Choose whether the HTTP response returns as soon as the worker is accepted or waits for final worker completion."),
                Field("options.profilingEnabled", "Profiling", "Capture a per-worker execution profile for diagnostics."),
                Field("subjectId.type", "Subject type", "Optional namespace for the subject identity, such as user, tenant, account, or order."),
                Field("subjectId.value", "Subject value", "Optional subject identifier used by idempotency, querying, and subject-scoped concurrency."),
            ]),
        new(
            "start",
            "Start",
            "Determines if queueing immediately starts the worker and how much progress the queue call waits for before returning.",
            [
                Field("options.configuration.start.policy", "Policy", "Sets the start policy for this worker."),
            ]),
        new(
            "idempotency",
            "Idempotency",
            "Prevents duplicate work from being accepted for the same subject while an existing matching worker is still active.",
            [
                Field("options.configuration.idempotency.isEnabled", "Enabled", "Turn on duplicate detection for subject-based queue requests."),
                Field("options.configuration.idempotency.storage", "Storage", "Choose whether duplicate detection is local to this runtime or backed by the configured persistence provider."),
                Field("subjectId.type", "Subject type", "Idempotency requires a subject so duplicates can be detected."),
                Field("subjectId.value", "Subject value", "Idempotency compares queued workers by this subject value."),
                Field("options.configuration.idempotency.conflictPolicy", "Conflict policy", "Defines what happens when a duplicate subject is detected."),
            ]),
        new(
            "recurrence",
            "Recurrence",
            "Runs the worker repeatedly on an interval, with guards for success and failure limits.",
            [
                Field("options.configuration.recurrence.isEnabled", "Enabled", "Turn the queued worker into a recurring worker instead of a single execution."),
                Field("options.configuration.recurrence.interval", "Interval", "Time between recurring executions. Use .NET TimeSpan text such as 00:05:00."),
                Field("options.configuration.recurrence.continueAfterFailure", "Continue after failure", "Keep scheduling future iterations after an iteration fails until limits stop it."),
                Field("options.configuration.recurrence.circuitBreakerFailureThreshold", "Circuit breaker threshold", "Number of failed iterations that opens the recurrence circuit breaker."),
                Field("options.configuration.recurrence.retainedSuccessfulIterations", "Retained successful iterations", "Number of successful iteration records retained on the worker."),
                Field("options.configuration.recurrence.retainedFailedIterations", "Retained failed iterations", "Number of failed iteration records retained on the worker."),
                Field("options.configuration.recurrence.raiseCircuitBreakerOpenedEvent", "Raise circuit event", "Emit a worker event when the recurrence circuit breaker opens."),
            ]),
        new(
            "retry",
            "Retry",
            "Retries execution failures that are classified as transient, using delay and backoff settings.",
            [
                Field("options.configuration.transientRetry.count", "Count", "Maximum number of transient retry attempts after the initial execution fails."),
                Field("options.configuration.transientRetry.initialDelay", "Initial delay", "Delay before the first retry. Use .NET TimeSpan text such as 00:00:00.8000000."),
                Field("options.configuration.transientRetry.jitter", "Jitter", "Random delay window added to retry timing."),
                Field("options.configuration.transientRetry.maximumDelay", "Maximum delay", "Upper bound for retry delay after backoff is applied."),
                Field("options.configuration.transientRetry.backoff", "Backoff", "Controls whether retry delays stay flat or grow exponentially between attempts."),
            ]),
        new(
            "logging",
            "Logging",
            "Controls worker log capture for execution diagnostics and worker snapshots.",
            [
                Field("options.configuration.logging.isEnabled", "Enabled", "Enable or disable Workable's buffered per-worker log capture."),
                Field("options.configuration.logging.level", "Level", "Minimum log level captured for this worker."),
                Field("options.configuration.logging.maximumBufferedEntries", "Maximum buffered entries", "Maximum number of log entries retained in the worker snapshot buffer."),
            ]),
        new(
            "retention",
            "Retention",
            "Controls how often completed or purged worker records are eligible for cleanup.",
            [
                Field("options.configuration.retention.purgeInterval", "Purge interval", "How long Workable should keep completed or canceled workers before automatic purge. Use .NET TimeSpan text such as 00:10:00."),
                Field("options.configuration.retention.maximumFinalWorkers", "Final worker target", "Approximate retained completed or canceled worker target for this worker group. Count cleanup runs in the background and can purge any final workers in the group."),
            ]),
        new(
            "concurrency",
            "Concurrency",
            "Limits how many matching workers may count as active at the same time.",
            [
                Field("options.configuration.concurrency.isEnabled", "Enabled", "Enable capacity limits for this work definition, subject, or concurrency key."),
                Field("options.configuration.concurrency.maximumCapacity", "Maximum capacity", "Maximum active workers allowed in the selected concurrency scope."),
                Field("options.configuration.concurrency.scope", "Scope", "Selects which workers are grouped together when counting capacity."),
                Field("options.configuration.concurrency.blockingMode", "Blocking mode", "Selects which worker states count against the active capacity limit."),
                Field("options.configuration.concurrency.limitReachedBehavior", "Limit reached", "Controls what happens when a queued worker would exceed the capacity limit."),
                Field("options.configuration.concurrency.overrideBehavior", "Override behavior", "Controls whether runtime configuration changes may temporarily exceed capacity."),
                Field("options.configuration.concurrency.storage", "Storage", "Choose whether concurrency is local to this runtime or backed by the configured persistence provider."),
            ]),
        new(
            "durability",
            "Durability",
            "Persists queue acceptance to the host configured durable queue store before committed work can start.",
            [
                Field("options.configuration.queueDurability.isEnabled", "Enabled", "Persist queue acceptance to the durable queue store before the worker is accepted into the runtime for execution."),
                Field("options.configuration.queueDurability.completeDurably", "Complete durably", "Commit successful work completion and durable queue cleanup in the same persistence transaction."),
                Field("options.configuration.idempotency.isEnabled", "Idempotency", "Enable subject-based duplicate detection for this queued worker."),
                Field("options.configuration.idempotency.storage", "Idempotency storage", "Use persistence-backed idempotency when duplicate detection must coordinate with durable queue rows."),
                Field("subjectId.type", "Subject type", "Idempotency requires a subject so duplicates can be detected."),
                Field("subjectId.value", "Subject value", "Idempotency compares queued workers by this subject value."),
            ]),
    ];

    private static WorkableHttpQueueRequestField Field(string path, string label, string description)
        => new(path, label, description);
}

public sealed record WorkableHttpQueueRequestTab(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<WorkableHttpQueueRequestField> Fields);

public sealed record WorkableHttpQueueRequestField(
    string Path,
    string Label,
    string Description);
