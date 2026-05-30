namespace Workable;

internal sealed class WorkerEventPublisher(
    WorkSystemId workSystemId,
    string? workSystemName,
    WorkEventStream events,
    Action<WorkerRecord> synchronize,
    IWorkSystemReadModelWriter? readModel = null)
{
    private const string PurgeEventType = "worker.purge";
    private static readonly IReadOnlySet<WorkIdentifier> EmptyIdentifiers = new HashSet<WorkIdentifier>();

    internal void Queued(WorkerRecord worker)
        => this.PublishWithoutSynchronize(
            worker,
            "worker.queued",
            details: new WorkerEventPayloadDetails(Origin: worker.Origin));

    internal void Started(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.started",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true));

    internal void ActionApplied(WorkerRecord worker, WorkActionOutcome outcome, WorkOrigin origin)
    {
        var action = outcome.Action;
        var eventType = $"worker.{action.ToString().ToLowerInvariant()}";
        var details = new WorkerEventPayloadDetails(
            Origin: action == WorkAction.Purge && !ShouldIncludeExplicitPurgeOrigin(origin) ? null : origin,
            Action: action,
            ActionStatus: outcome.Status);
        if (action == WorkAction.Purge)
        {
            if (outcome.IsAccepted)
            {
                this.PublishWorkerPurge(worker, ShouldIncludeExplicitPurgeOrigin(origin) ? origin : null);
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
            details: new WorkerEventPayloadDetails(
                Origin: origin,
                ReconfigurationStatus: outcome.Status,
                Reconfiguration: changes));

    internal void CompletionRecorded(WorkerRecord worker, WorkCompletionStatus status)
        => this.Publish(
            worker,
            EventTypeFor(status),
            details: new WorkerEventPayloadDetails(
                CompletionStatus: status,
                IncludeLatestIteration: true));

    internal void Waiting(WorkerRecord worker)
    {
        var recurrenceInterval = worker.GetConfiguration().Recurrence.Interval;
        this.Publish(
            worker,
            "worker.waiting",
                details: new WorkerEventPayloadDetails(
                    IncludeLatestIteration: true,
                    RecurrenceInterval: recurrenceInterval));
    }

    internal void Retrying(WorkerRecord worker, TimeSpan retryDelay)
        => this.Publish(
            worker,
            "worker.retrying",
            details: new WorkerEventPayloadDetails(
                IncludeLatestIteration: true,
                RetryDelay: retryDelay));

    internal void IterationStarted(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.started",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true));

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
                CompletionStatus: WorkCompletionStatus.Failed,
                IncludeLatestIteration: true));

    internal void Purged(WorkerRecord worker)
        => this.PublishWorkerPurge(worker);

    internal void Purged(
        IReadOnlyCollection<WorkerId> workerIds,
        WorkDefinitionId? definitionId,
        string? definitionName)
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
                workSystemName,
                workerId,
                definitionId,
                definitionName,
                purgedWorkerIds,
                null),
            static state => new WorkEvent(
                state.OccurredAt,
                state.WorkSystemId,
                state.WorkSystemName,
                state.WorkerId,
                state.DefinitionId,
                state.DefinitionName,
                null,
                null,
                EmptyIdentifiers,
                PurgeEventType,
                WorkerEventPayloads.CreatePurge(state.WorkerIds, state.OccurredAt, state.Origin)));
    }

    internal void Log(WorkerRecord worker, WorkerLogEntry entry)
    {
        readModel?.RecordWorker(worker.ToReadModelWorker());
        events.Publish(
            worker.ToEventMetadata(workSystemId, "worker.log"),
            (WorkSystemId: workSystemId, WorkSystemName: workSystemName, Worker: worker, Entry: entry),
            static state => state.Worker.ToLogEvent(state.WorkSystemId, state.WorkSystemName, state.Entry));
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
            (WorkSystemId: workSystemId, WorkSystemName: workSystemName, Worker: worker, EventType: eventType, Details: details),
            static state => state.Worker.ToEvent(
                state.WorkSystemId,
                state.WorkSystemName,
                state.EventType,
                state.Details));
    }

    private void PublishWorkerPurge(WorkerRecord worker, WorkOrigin? origin = null)
    {
        readModel?.ForgetWorker(worker.Id);

        var occurredAt = DateTimeOffset.UtcNow;
        events.Publish(
            worker.ToEventMetadata(workSystemId, PurgeEventType),
            new WorkerPurgeEventState(occurredAt, workSystemId, workSystemName, worker, origin),
            static state => new WorkEvent(
                state.OccurredAt,
                state.WorkSystemId,
                state.WorkSystemName,
                state.Worker.Id,
                state.Worker.Work.Definition.Id,
                state.Worker.Work.Definition.Name,
                state.Worker.SubjectId,
                state.Worker.ConcurrencyKey,
                state.Worker.Identifiers,
                PurgeEventType,
                WorkerEventPayloads.CreatePurge(new[] { state.Worker.Id }, state.OccurredAt, state.Origin)));
    }

    private sealed record PurgeEventState(
        DateTimeOffset OccurredAt,
        WorkSystemId WorkSystemId,
        string? WorkSystemName,
        WorkerId? WorkerId,
        WorkDefinitionId? DefinitionId,
        string? DefinitionName,
        IReadOnlyList<WorkerId> WorkerIds,
        WorkOrigin? Origin);

    private sealed record WorkerPurgeEventState(
        DateTimeOffset OccurredAt,
        WorkSystemId WorkSystemId,
        string? WorkSystemName,
        WorkerRecord Worker,
        WorkOrigin? Origin);

    private static bool ShouldIncludeExplicitPurgeOrigin(WorkOrigin origin)
        => !string.IsNullOrWhiteSpace(origin.Actor.Id) ||
            !string.IsNullOrWhiteSpace(origin.Actor.Name) ||
            !string.IsNullOrWhiteSpace(origin.Actor.Email);
}
