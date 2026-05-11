namespace Workable;

internal sealed class WorkerEventPublisher(
    WorkSystemId workSystemId,
    WorkEventStream events,
    Action<WorkerRecord> synchronize)
{
    public void Queued(WorkerRecord worker)
        => this.Publish(worker, "worker.queued", details: new WorkerEventPayloadDetails(IncludeInput: true));

    public void Started(WorkerRecord worker)
        => this.Publish(worker, "worker.started", details: new WorkerEventPayloadDetails(IncludeInput: true));

    public void ActionApplied(WorkerRecord worker, WorkActionOutcome outcome, WorkOrigin origin)
    {
        var action = outcome.Action;
        var eventType = $"worker.{action.ToString().ToLowerInvariant()}";
        var details = new WorkerEventPayloadDetails(
            Action: action,
            ActionStatus: outcome.Status,
            IncludeOutput: true);
        if (action == WorkAction.Purge)
        {
            this.PublishWithoutSynchronize(worker, eventType, origin, details);
            return;
        }

        this.Publish(worker, eventType, origin, details);
    }

    public void Reconfigured(
        WorkerRecord worker,
        WorkerReconfiguration changes,
        WorkActionOutcome outcome,
        WorkOrigin origin)
        => this.Publish(
            worker,
            "worker.reconfigured",
            origin,
            new WorkerEventPayloadDetails(
                ReconfigurationStatus: outcome.Status,
                Reconfiguration: changes));

    public void CompletionRecorded(WorkerRecord worker, WorkCompletionStatus status)
        => this.Publish(
            worker,
            EventTypeFor(status),
            details: new WorkerEventPayloadDetails(
                IncludeOutput: true,
                CompletionStatus: status));

    public void Waiting(WorkerRecord worker)
    {
        var recurrenceInterval = worker.GetConfiguration().Recurrence.Interval;
        this.Publish(
            worker,
            "worker.waiting",
            details: new WorkerEventPayloadDetails(RecurrenceInterval: recurrenceInterval));
    }

    public void RecurringIterationCompleted(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.completed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Completed,
                IncludeLatestIteration: true));

    public void RecurringIterationFailed(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.failed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Failed,
                IncludeLatestIteration: true));

    public void RecurrenceCircuitOpened(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.recurrence.circuit_opened",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true));

    public void Failed(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.failed",
            details: new WorkerEventPayloadDetails(
                IncludeOutput: true,
                CompletionStatus: WorkCompletionStatus.Failed));

    public void Purged(WorkerRecord worker)
        => this.PublishWithoutSynchronize(worker, "worker.purge");

    public void Log(WorkerRecord worker, WorkerLogEntry entry)
        => events.Publish(worker.ToLogEvent(workSystemId, entry));

    internal static string EventTypeFor(WorkCompletionStatus status)
        => status == WorkCompletionStatus.Completed
            ? "worker.completed"
            : $"worker.{status.ToString().ToLowerInvariant()}";

    private void Publish(
        WorkerRecord worker,
        string eventType,
        WorkOrigin? origin = null,
        WorkerEventPayloadDetails? details = null)
    {
        synchronize(worker);
        events.Publish(worker.ToEvent(workSystemId, eventType, origin, details));
    }

    private void PublishWithoutSynchronize(
        WorkerRecord worker,
        string eventType,
        WorkOrigin? origin = null,
        WorkerEventPayloadDetails? details = null)
        => events.Publish(worker.ToEvent(workSystemId, eventType, origin, details));
}
