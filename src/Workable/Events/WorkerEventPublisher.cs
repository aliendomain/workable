namespace Workable;

internal sealed class WorkerEventPublisher(
    WorkSystemId workSystemId,
    WorkEventStream events,
    Action<WorkerRecord> synchronize,
    IWorkSystemReadModelWriter? readModel = null)
{
    private const string PurgeEventType = "worker.purge";
    private static readonly IReadOnlySet<WorkIdentifier> EmptyIdentifiers = new HashSet<WorkIdentifier>();

    internal void Queued(WorkerRecord worker)
        => this.PublishWithoutSynchronize(worker, "worker.queued");

    internal void Started(WorkerRecord worker)
        => this.Publish(worker, "worker.started");

    internal void ActionApplied(WorkerRecord worker, WorkActionOutcome outcome, WorkOrigin origin)
    {
        var action = outcome.Action;
        var eventType = $"worker.{action.ToString().ToLowerInvariant()}";
        var details = new WorkerEventPayloadDetails(
            Action: action,
            ActionStatus: outcome.Status);
        if (action == WorkAction.Purge)
        {
            if (outcome.IsAccepted)
            {
                this.PublishWorkerPurge(worker, origin);
            }

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
                CompletionStatus: WorkCompletionStatus.Failed));

    internal void Purged(WorkerRecord worker)
        => this.PublishWorkerPurge(worker);

    internal void Purged(IReadOnlyCollection<WorkerId> workerIds, WorkDefinitionId? definitionId)
    {
        ArgumentNullException.ThrowIfNull(workerIds);

        if (workerIds.Count == 0)
        {
            return;
        }

        var purgedWorkerIds = workerIds.Distinct().ToArray();
        if (purgedWorkerIds.Length == 0)
        {
            return;
        }

        readModel?.ForgetWorkers(purgedWorkerIds);

        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = purgedWorkerIds.Length == 1 ? purgedWorkerIds[0] : (WorkerId?)null;
        events.Publish(
            new WorkEventMetadata(
                workSystemId,
                workerId,
                definitionId,
                subjectId: null,
                concurrencyKey: null,
                PurgeEventType),
            new PurgeEventState(
                occurredAt,
                workSystemId,
                workerId,
                definitionId,
                purgedWorkerIds),
            static state => new WorkEvent(
                state.OccurredAt,
                state.WorkSystemId,
                state.WorkerId,
                state.DefinitionId,
                null,
                null,
                EmptyIdentifiers,
                null,
                PurgeEventType,
                WorkerEventPayloads.CreatePurge(state.WorkerIds, state.OccurredAt),
                []));
    }

    internal void Log(WorkerRecord worker, WorkerLogEntry entry)
    {
        readModel?.RecordWorker(worker.ToReadModelWorker());
        events.Publish(
            worker.ToEventMetadata(workSystemId, "worker.log"),
            (WorkSystemId: workSystemId, Worker: worker, Entry: entry),
            static state => state.Worker.ToLogEvent(state.WorkSystemId, state.Entry));
    }

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
    {
        if (eventType == PurgeEventType)
        {
            this.PublishWorkerPurge(worker, origin);
            return;
        }

        readModel?.RecordWorker(worker.ToReadModelWorker());

        events.Publish(
            worker.ToEventMetadata(workSystemId, eventType),
            (WorkSystemId: workSystemId, Worker: worker, EventType: eventType, Origin: origin, Details: details),
            static state => state.Worker.ToEvent(
                state.WorkSystemId,
                state.EventType,
                state.Origin,
                state.Details));
    }

    private void PublishWorkerPurge(WorkerRecord worker, WorkOrigin? origin = null)
    {
        readModel?.ForgetWorker(worker.Id);

        var occurredAt = DateTimeOffset.UtcNow;
        events.Publish(
            worker.ToEventMetadata(workSystemId, PurgeEventType),
            new WorkerPurgeEventState(occurredAt, workSystemId, worker, origin),
            static state => new WorkEvent(
                state.OccurredAt,
                state.WorkSystemId,
                state.Worker.Id,
                state.Worker.Work.Definition.Id,
                state.Worker.SubjectId,
                state.Worker.ConcurrencyKey,
                state.Worker.Identifiers,
                state.Origin,
                PurgeEventType,
                WorkerEventPayloads.CreatePurge(new[] { state.Worker.Id }, state.OccurredAt),
                []));
    }

    private sealed record PurgeEventState(
        DateTimeOffset OccurredAt,
        WorkSystemId WorkSystemId,
        WorkerId? WorkerId,
        WorkDefinitionId? DefinitionId,
        IReadOnlyList<WorkerId> WorkerIds);

    private sealed record WorkerPurgeEventState(
        DateTimeOffset OccurredAt,
        WorkSystemId WorkSystemId,
        WorkerRecord Worker,
        WorkOrigin? Origin);
}
