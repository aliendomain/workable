namespace Workable;

internal sealed class WorkerEventPublisher(
    WorkSystemId workSystemId,
    WorkEventStream events,
    Action<WorkerRecord> synchronize)
{
    internal void Queued(WorkerRecord worker)
        => this.PublishWithoutSynchronize(worker, "worker.queued", details: new WorkerEventPayloadDetails(IncludeInput: true));

    internal void Started(WorkerRecord worker)
        => this.Publish(worker, "worker.started", details: new WorkerEventPayloadDetails(IncludeInput: true));

    internal void ActionApplied(WorkerRecord worker, WorkActionOutcome outcome, WorkOrigin origin)
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

    internal void Reconfigured(
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

    internal void CompletionRecorded(WorkerRecord worker, WorkCompletionStatus status)
        => this.Publish(
            worker,
            EventTypeFor(status),
            details: new WorkerEventPayloadDetails(
                IncludeOutput: true,
                CompletionStatus: status));

    internal void Waiting(WorkerRecord worker)
    {
        var recurrenceInterval = worker.GetConfiguration().Recurrence.Interval;
        this.Publish(
            worker,
            "worker.waiting",
                details: new WorkerEventPayloadDetails(RecurrenceInterval: recurrenceInterval));
    }

    internal void Retrying(WorkerRecord worker, TimeSpan retryDelay)
        => this.Publish(
            worker,
            "worker.retrying",
            details: new WorkerEventPayloadDetails(RetryDelay: retryDelay));

    internal void IterationCompleted(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.completed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Completed,
                IncludeLatestIteration: true));

    internal void IterationFailed(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.failed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Failed,
                IncludeLatestIteration: true));

    internal void RecurrenceCircuitOpened(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.recurrence.circuit_opened",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true));

    internal void Failed(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.failed",
            details: new WorkerEventPayloadDetails(
                IncludeOutput: true,
                CompletionStatus: WorkCompletionStatus.Failed));

    internal void Purged(WorkerRecord worker)
        => this.PublishWithoutSynchronize(worker, "worker.purge");

    internal void Log(WorkerRecord worker, WorkerLogEntry entry)
        => events.Publish(
            (WorkSystemId: workSystemId, Worker: worker, Entry: entry),
            static state => state.Worker.ToLogEvent(state.WorkSystemId, state.Entry));

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
        this.PublishWithoutSynchronize(worker, eventType, origin, details);
    }

    private void PublishWithoutSynchronize(
        WorkerRecord worker,
        string eventType,
        WorkOrigin? origin = null,
        WorkerEventPayloadDetails? details = null)
        => events.Publish(
            (WorkSystemId: workSystemId, Worker: worker, EventType: eventType, Origin: origin, Details: details),
            static state => state.Worker.ToEvent(
                state.WorkSystemId,
                state.EventType,
                state.Origin,
                state.Details));
}
