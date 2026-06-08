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
            details: new WorkerEventPayloadDetails(RequestContext: worker.RequestContext));

    internal void Started(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.started",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true),
            recordReadModel: false);

    internal void ActionApplied(WorkerRecord worker, WorkActionOutcome outcome, WorkRequestContext requestContext)
    {
        var action = outcome.Action;
        var eventType = $"worker.{action.ToString().ToLowerInvariant()}";
        var details = new WorkerEventPayloadDetails(
            RequestContext: action == WorkAction.Purge && !ShouldIncludeExplicitPurgeOrigin(requestContext) ? null : requestContext,
            Action: action,
            ActionStatus: outcome.Status);
        if (action == WorkAction.Purge)
        {
            if (outcome.IsAccepted)
            {
                this.PublishWorkerPurge(worker, ShouldIncludeExplicitPurgeOrigin(requestContext) ? requestContext : null);
            }

            return;
        }

        this.Publish(
            worker,
            eventType,
            requestContext,
            details,
            recordReadModel: action != WorkAction.Start || !outcome.IsAccepted);
    }

    internal void Reconfigured(
        WorkerRecord worker,
        WorkerReconfiguration changes,
        WorkActionOutcome outcome,
        WorkRequestContext requestContext)
        => this.Publish(
            worker,
            "worker.reconfigured",
            details: new WorkerEventPayloadDetails(
                RequestContext: requestContext,
                ReconfigurationStatus: outcome.Status,
                Reconfiguration: changes));

    internal void CompletionRecorded(
        WorkerRecord worker,
        WorkCompletionStatus status,
        bool recordReadModel = true)
        => this.Publish(
            worker,
            EventTypeFor(status),
            details: new WorkerEventPayloadDetails(
                CompletionStatus: status,
                IncludeLatestIteration: true),
            recordReadModel: recordReadModel);

    internal void Waiting(WorkerRecord worker)
    {
        var recurrenceInterval = worker.GetConfiguration().Recurrence.Interval;
        this.Publish(
            worker,
            "worker.waiting",
                details: new WorkerEventPayloadDetails(
                    IncludeLatestIteration: true,
                    RecurrenceInterval: recurrenceInterval),
                recordReadModel: false);
    }

    internal void Retrying(WorkerRecord worker, TimeSpan retryDelay)
        => this.Publish(
            worker,
            "worker.retrying",
            details: new WorkerEventPayloadDetails(
                IncludeLatestIteration: true,
                RetryDelay: retryDelay),
            recordReadModel: false);

    internal void IterationStarted(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.started",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true),
            recordReadModel: false);

    internal void IterationCompleted(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.completed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Completed,
                IncludeLatestIteration: true),
            recordReadModel: false);

    internal void IterationFailed(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.iteration.failed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Failed,
                IncludeLatestIteration: true),
            recordReadModel: false);

    internal void RecurrenceCircuitOpened(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.recurrence.circuit_opened",
            details: new WorkerEventPayloadDetails(IncludeLatestIteration: true),
            recordReadModel: false);

    internal void Failed(WorkerRecord worker)
        => this.Publish(
            worker,
            "worker.failed",
            details: new WorkerEventPayloadDetails(
                CompletionStatus: WorkCompletionStatus.Failed,
                IncludeLatestIteration: true),
            recordReadModel: false);

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
            new PurgeEventState(
                occurredAt,
                workSystemId,
                workSystemName,
                workerId,
                definitionId,
                definitionName,
                purgedWorkerIds,
                null),
            static state => new WorkEventMetadata(
                state.WorkSystemId,
                state.WorkerId,
                state.DefinitionId,
                state.DefinitionName,
                subjectId: null,
                concurrencyKey: null,
                PurgeEventType),
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
                WorkerEventPayloads.CreatePurge(state.WorkerIds, state.OccurredAt, state.RequestContext)));
    }

    internal void Log(WorkerRecord worker, WorkerLogEntry entry)
    {
        readModel?.RecordWorker(worker.ToReadModelWorker());
        events.Publish(
            (WorkSystemId: workSystemId, WorkSystemName: workSystemName, Worker: worker, Entry: entry),
            static state => state.Worker.ToEventMetadata(state.WorkSystemId, "worker.log"),
            static state => state.Worker.ToLogEvent(state.WorkSystemId, state.WorkSystemName, state.Entry));
    }

    internal static string EventTypeFor(WorkCompletionStatus status)
        => status == WorkCompletionStatus.Completed
            ? "worker.completed"
            : $"worker.{status.ToString().ToLowerInvariant()}";

    private void Publish(
        WorkerRecord worker,
        string eventType,
        WorkRequestContext? requestContext = null,
        WorkerEventPayloadDetails? details = null,
        bool recordReadModel = true)
    {
        synchronize(worker);
        this.PublishWithoutSynchronize(worker, eventType, requestContext, details, recordReadModel);
    }

    private void PublishWithoutSynchronize(
        WorkerRecord worker,
        string eventType,
        WorkRequestContext? requestContext = null,
        WorkerEventPayloadDetails? details = null,
        bool recordReadModel = true)
    {
        if (eventType == PurgeEventType)
        {
            this.PublishWorkerPurge(worker, requestContext);
            return;
        }

        if (recordReadModel)
        {
            readModel?.RecordWorker(worker.ToReadModelWorker());
        }

        events.Publish(
            (WorkSystemId: workSystemId, WorkSystemName: workSystemName, Worker: worker, EventType: eventType, Details: details),
            static state => state.Worker.ToEventMetadata(state.WorkSystemId, state.EventType),
            static state => state.Worker.ToEvent(
                state.WorkSystemId,
                state.WorkSystemName,
                state.EventType,
                state.Details));
    }

    private void PublishWorkerPurge(WorkerRecord worker, WorkRequestContext? requestContext = null)
    {
        readModel?.ForgetWorker(worker.Id);

        var occurredAt = DateTimeOffset.UtcNow;
        events.Publish(
            new WorkerPurgeEventState(occurredAt, workSystemId, workSystemName, worker, requestContext),
            static state => state.Worker.ToEventMetadata(state.WorkSystemId, PurgeEventType),
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
                WorkerEventPayloads.CreatePurge(new[] { state.Worker.Id }, state.OccurredAt, state.RequestContext)));
    }

    private sealed record PurgeEventState(
        DateTimeOffset OccurredAt,
        WorkSystemId WorkSystemId,
        string? WorkSystemName,
        WorkerId? WorkerId,
        WorkDefinitionId? DefinitionId,
        string? DefinitionName,
        IReadOnlyList<WorkerId> WorkerIds,
        WorkRequestContext? RequestContext);

    private sealed record WorkerPurgeEventState(
        DateTimeOffset OccurredAt,
        WorkSystemId WorkSystemId,
        string? WorkSystemName,
        WorkerRecord Worker,
        WorkRequestContext? RequestContext);

    private static bool ShouldIncludeExplicitPurgeOrigin(WorkRequestContext requestContext)
        => !string.IsNullOrWhiteSpace(requestContext.Actor.Id) ||
            !string.IsNullOrWhiteSpace(requestContext.Actor.Name) ||
            !string.IsNullOrWhiteSpace(requestContext.Actor.Email);
}
