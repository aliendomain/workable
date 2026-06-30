using System.Text.Json;

namespace Workable;

internal static class WorkflowEventPayloads
{
    public static JsonElement Create(
        WorkflowRunSnapshot run,
        WorkflowStepRunSnapshot? step = null,
        WorkRequestContext? requestContext = null,
        WorkflowAction? action = null,
        WorkflowActionStatus? actionStatus = null,
        IReadOnlyList<WorkMessage>? messages = null)
    {
        return JsonSerializer.SerializeToElement(
            new WorkflowEventPayload(
                WorkflowEventRunPayload.From(run),
                step is null ? null : WorkflowEventStepPayload.From(step),
                requestContext is null ? null : WorkflowEventOriginPayload.From(requestContext),
                action,
                actionStatus,
                messages),
            WorkEventJson.Options);
    }

    private sealed record WorkflowEventPayload(
        WorkflowEventRunPayload Run,
        WorkflowEventStepPayload? Step = null,
        WorkflowEventOriginPayload? Origin = null,
        WorkflowAction? Action = null,
        WorkflowActionStatus? ActionStatus = null,
        IReadOnlyList<WorkMessage>? Messages = null);

    private sealed record WorkflowEventRunPayload(
        WorkflowRunId Id,
        string DefinitionName,
        WorkflowRunStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        string? CurrentStepName,
        WorkflowStepKind? CurrentStepKind,
        WorkflowStepRunStatus? CurrentStepStatus,
        int OutstandingChildWorkerCount)
    {
        public static WorkflowEventRunPayload From(WorkflowRunSnapshot run)
        {
            var currentStep = ResolveCurrentStep(run);
            return new WorkflowEventRunPayload(
                run.Id,
                run.DefinitionName,
                run.Status,
                run.CreatedAt,
                run.StartedAt,
                run.CompletedAt,
                currentStep?.Name,
                currentStep?.Kind,
                currentStep?.Status,
                CountOutstandingChildren(run));
        }

        private static WorkflowStepRunSnapshot? ResolveCurrentStep(WorkflowRunSnapshot run)
            => run.Steps.FirstOrDefault(step => step.Status == WorkflowStepRunStatus.Failed)
                ?? run.Steps.LastOrDefault(step => step.Status == WorkflowStepRunStatus.Running)
                ?? run.Steps.LastOrDefault(step => step.Status == WorkflowStepRunStatus.Completed);

        private static int CountOutstandingChildren(WorkflowRunSnapshot run)
        {
            var outstanding = 0;
            foreach (var step in run.Steps)
            {
                switch (step.Kind)
                {
                    case WorkflowStepKind.DispatchWork:
                    case WorkflowStepKind.DispatchEach:
                    case WorkflowStepKind.Parallel:
                        if (step.Status == WorkflowStepRunStatus.Completed)
                        {
                            outstanding += step.WorkerIds.Count(workerId =>
                                !run.ChildReceipts.Any(receipt =>
                                    receipt.WorkerId == workerId &&
                                    receipt.CompletionStatus == WorkCompletionStatus.Completed));
                        }

                        break;
                    case WorkflowStepKind.Join:
                        if (step.Status == WorkflowStepRunStatus.Completed)
                        {
                            outstanding = 0;
                        }
                        else if (step.Status == WorkflowStepRunStatus.Running && step.WorkerIds.Count > 0)
                        {
                            outstanding = step.WorkerIds.Count(workerId =>
                                !run.ChildReceipts.Any(receipt =>
                                    receipt.WorkerId == workerId &&
                                    receipt.CompletionStatus == WorkCompletionStatus.Completed));
                        }

                        break;
                }
            }

            return outstanding;
        }
    }

    private sealed record WorkflowEventStepPayload(
        string Name,
        WorkflowStepKind Kind,
        WorkflowStepRunStatus Status,
        int WorkerCount,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt)
    {
        public static WorkflowEventStepPayload From(WorkflowStepRunSnapshot step)
            => new(
                step.Name,
                step.Kind,
                step.Status,
                step.WorkerIds.Count,
                step.StartedAt,
                step.CompletedAt);
    }

    private sealed record WorkflowEventOriginPayload(
        string Channel,
        string Surface,
        WorkflowEventOriginActorPayload? Actor,
        string? Description = null,
        string? Url = null)
    {
        public static WorkflowEventOriginPayload From(WorkRequestContext requestContext)
            => new(
                requestContext.Channel.ToString(),
                requestContext.Surface.ToString(),
                WorkflowEventOriginActorPayload.From(requestContext.Actor),
                requestContext.Description,
                requestContext.Url);
    }

    private sealed record WorkflowEventOriginActorPayload(
        string? Id,
        string? Name,
        string? Email)
    {
        public static WorkflowEventOriginActorPayload? From(WorkActor actor)
            => string.IsNullOrWhiteSpace(actor.Id) &&
                string.IsNullOrWhiteSpace(actor.Name) &&
                string.IsNullOrWhiteSpace(actor.Email)
                ? null
                : new WorkflowEventOriginActorPayload(actor.Id, actor.Name, actor.Email);
    }
}
