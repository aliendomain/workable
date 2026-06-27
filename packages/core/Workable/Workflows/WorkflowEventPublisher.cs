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
            action == WorkflowAction.Cancel ? "workflow.cancel" : "workflow.stop",
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
        var identifiers = CreateIdentifiers(snapshot, step);
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
                () => state.Identifiers),
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
                    state.Messages)));
    }

    private static IReadOnlySet<WorkIdentifier> CreateIdentifiers(
        WorkflowRunSnapshot snapshot,
        WorkflowStepRunSnapshot? step)
    {
        var identifiers = new HashSet<WorkIdentifier>
        {
            new("workflow-run", snapshot.Id.Value.ToString("D")),
            new("workflow-definition", snapshot.DefinitionName),
        };
        if (step is not null)
        {
            identifiers.Add(new WorkIdentifier("workflow-step", step.Name));
        }

        return identifiers;
    }

    private sealed record WorkflowEventState(
        WorkflowRunSnapshot Snapshot,
        WorkflowStepRunSnapshot? Step,
        WorkRequestContext? RequestContext,
        WorkflowAction? Action,
        WorkflowActionStatus? ActionStatus,
        IReadOnlyList<WorkMessage>? Messages,
        IReadOnlySet<WorkIdentifier> Identifiers);
}
