namespace Workable;

internal sealed class WorkflowEventPublisher(
    WorkSystemId workSystemId,
    string? workSystemName,
    WorkEventStream events)
{
    internal void Started(WorkflowRunSnapshot snapshot, WorkRequestContext requestContext)
        => this.Publish(snapshot, "workflow.started", requestContext: requestContext);

    internal void ActionAccepted(
        WorkflowRunSnapshot snapshot,
        WorkflowAction action,
        WorkRequestContext requestContext)
        => this.Publish(
            snapshot,
            action switch
            {
                WorkflowAction.Start => "workflow.resume",
                WorkflowAction.Pause => "workflow.pause",
                _ => "workflow.cancel",
            },
            requestContext: requestContext,
            action: action,
            actionStatus: WorkflowActionStatus.Accepted);

    internal void StepUpdated(WorkflowRunSnapshot snapshot, string stepName)
        => this.Publish(snapshot, "workflow.step.updated", stepName: stepName);

    internal void Completion(WorkflowRunCompletion completion)
    {
        if (completion.Run is null)
        {
            return;
        }

        var eventType = completion.Status switch
        {
            WorkflowRunStatus.Completed => "workflow.completed",
            WorkflowRunStatus.Paused => "workflow.paused",
            WorkflowRunStatus.Blocked => "workflow.blocked",
            WorkflowRunStatus.Failed => "workflow.failed",
            WorkflowRunStatus.Canceled => "workflow.canceled",
            _ => "workflow.updated",
        };

        this.Publish(
            completion.Run,
            eventType,
            messages: completion.Messages.Count > 0 ? completion.Messages : null);
    }

    private void Publish(
        WorkflowRunSnapshot snapshot,
        string eventType,
        string? stepName = null,
        WorkRequestContext? requestContext = null,
        WorkflowAction? action = null,
        WorkflowActionStatus? actionStatus = null,
        IReadOnlyList<WorkMessage>? messages = null)
    {
        var step = string.IsNullOrWhiteSpace(stepName)
            ? null
            : snapshot.Steps.SingleOrDefault(candidate => string.Equals(candidate.Name, stepName, StringComparison.Ordinal));
        var identifiers = CreateIdentifiers(snapshot);
        events.Publish(
            new WorkflowEventState(snapshot, step, requestContext, action, actionStatus, messages, identifiers),
            state => new WorkEventMetadata(
                workSystemId,
                workerId: null,
                definitionId: null,
                definitionName: state.Snapshot.DefinitionName,
                subjectId: null,
                concurrencyKey: null,
                eventType,
                () => state.Identifiers,
                WorkEventDefinitionKind.Workflow,
                state.Snapshot.DefinitionId),
            state => new WorkEvent(
                DateTimeOffset.UtcNow,
                workSystemId,
                workSystemName,
                workerId: null,
                workDefinitionId: null,
                workDefinitionName: state.Snapshot.DefinitionName,
                subjectId: null,
                concurrencyKey: null,
                identifiers: state.Identifiers,
                eventType,
                WorkflowEventPayloads.Create(
                    state.Snapshot,
                    state.Step,
                    state.RequestContext,
                    state.Action,
                    state.ActionStatus,
                    state.Messages),
                WorkEventDefinitionKind.Workflow,
                state.Snapshot.DefinitionId));
    }

    private static IReadOnlySet<WorkIdentifier> CreateIdentifiers(WorkflowRunSnapshot snapshot)
        => new HashSet<WorkIdentifier>
        {
            new("workflow-run", snapshot.Id.Value.ToString("D")),
        };

    private sealed record WorkflowEventState(
        WorkflowRunSnapshot Snapshot,
        WorkflowStepRunSnapshot? Step,
        WorkRequestContext? RequestContext,
        WorkflowAction? Action,
        WorkflowActionStatus? ActionStatus,
        IReadOnlyList<WorkMessage>? Messages,
        IReadOnlySet<WorkIdentifier> Identifiers);
}
