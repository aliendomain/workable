namespace Workable;

internal static class WorkMessageAccessFilter
{
    private const string UnhandledExceptionCode = "workable.execution.exception";
    private const string WorkflowUnhandledExceptionCode = "workable.workflow.execution_exception";
    private const string QueueDurabilityDuplicateCode = "workable.queue_durability.duplicate";
    private const string QueueDurabilityStoreUnreachableCode = "workable.queue_durability.store_unreachable";
    private const string IdempotencyDuplicateSubjectCode = "workable.idempotency.duplicate_subject";
    private const string IdempotencyStoreUnreachableCode = "workable.idempotency.persistence_store_unreachable";
    private const string SafeUnhandledExceptionText = "Work execution failed with an unhandled exception.";
    private const string SafeWorkflowUnhandledExceptionText = "Workflow execution failed with an unhandled exception.";
    private const string SafeQueueDurabilityDuplicateText = "The durable queue rejected this request as a duplicate.";
    private const string SafeQueueDurabilityStoreUnreachableText = "The persistence store required for durable queueing is currently unavailable.";
    private const string SafeIdempotencyDuplicateSubjectText = "A work request with the same idempotency subject already exists.";
    private const string SafeIdempotencyStoreUnreachableText = "The persistence store required for idempotency is currently unavailable.";

    public static WorkQueueOutcome Apply(
        WorkQueueOutcome outcome,
        bool canReadRetainedDetails)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var messages = Apply(outcome.Messages, canReadRetainedDetails);
        return ReferenceEquals(messages, outcome.Messages)
            ? outcome
            : outcome with { Messages = messages };
    }

    public static IReadOnlyList<WorkMessage> Apply(
        IReadOnlyList<WorkMessage> messages,
        bool canReadRetainedDetails)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (canReadRetainedDetails)
        {
            return messages;
        }

        List<WorkMessage>? filtered = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var safeText = message.Code switch
            {
                UnhandledExceptionCode => SafeUnhandledExceptionText,
                WorkflowUnhandledExceptionCode => SafeWorkflowUnhandledExceptionText,
                QueueDurabilityDuplicateCode => SafeQueueDurabilityDuplicateText,
                QueueDurabilityStoreUnreachableCode => SafeQueueDurabilityStoreUnreachableText,
                IdempotencyDuplicateSubjectCode => SafeIdempotencyDuplicateSubjectText,
                IdempotencyStoreUnreachableCode => SafeIdempotencyStoreUnreachableText,
                _ => null,
            };
            if (safeText is null)
            {
                filtered?.Add(message);
                continue;
            }

            filtered ??= [.. messages.Take(index)];
            filtered.Add(message with
            {
                Text = safeText,
                Metadata = null,
            });
        }

        return filtered ?? messages;
    }
}
