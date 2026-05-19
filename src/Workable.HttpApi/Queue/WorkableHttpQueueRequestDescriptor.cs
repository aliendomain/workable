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
            "Rejects duplicate queue requests for the same work definition and subject.",
            [
                Field("options.configuration.idempotency.isEnabled", "Enabled", "Reject a second worker with the same work definition and subject while the first worker is still protected."),
                Field("options.configuration.idempotency.storage", "Storage", "Local checks only this running host. Persistence stores the reservation so restarts and other hosts share the duplicate check."),
                Field("subjectId.type", "Subject type", "The subject namespace, such as order or tenant. Type and value together identify the thing being protected."),
                Field("subjectId.value", "Subject value", "The specific subject id. Workers with the same definition, subject type, and subject value are duplicates."),
                Field("options.configuration.idempotency.conflictPolicy", "Conflict policy", "When a duplicate is found, reject the new queue request instead of creating another worker."),
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
            "Limits how many matching workers can occupy execution capacity at the same time.",
            [
                Field("options.configuration.concurrency.isEnabled", "Enabled", "Check capacity before this worker starts."),
                Field("options.configuration.concurrency.maximumCapacity", "Maximum capacity", "How many workers in the same concurrency group may count as active at once."),
                Field("options.configuration.concurrency.scope", "Scope", "Defines the group that shares capacity: this definition, this subject, or this concurrency key."),
                Field("options.configuration.concurrency.blockingMode", "Blocking mode", "Which states occupy capacity. WhileExecuting counts running execution; broader modes also count paused or failed workers."),
                Field("options.configuration.concurrency.limitReachedBehavior", "Limit reached", "When capacity is full, either reject the queue request or accept it and leave it queued until a slot opens."),
                Field("options.configuration.concurrency.overrideBehavior", "Override behavior", "For manual starts and reconfiguration, choose whether Workable may temporarily exceed capacity or must enforce it strictly."),
                Field("options.configuration.concurrency.storage", "Storage", "Local enforces capacity within this host. Persistence enforces capacity in the shared store while durable workers are dispatched."),
            ]),
        new(
            "durability",
            "Durability",
            "Stores accepted queue requests so work can survive process shutdown and restart. Duplicate protection is configured on the Idempotency tab.",
            [
                Field("options.configuration.queueDurability.isEnabled", "Enabled", "Store the queued worker before returning accepted. If the caller supplies a transaction, the worker starts only after that transaction commits."),
                Field("options.configuration.queueDurability.completeDurably", "Complete durably", "Executor code must complete with its persistence transaction, so business data and Workable completion commit or roll back together."),
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
