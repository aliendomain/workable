using System.Collections.Concurrent;

namespace Workable;

internal sealed class InMemoryWorkflowRuntime(
    string? systemName,
    bool requiresAuthorization,
    WorkflowCatalog catalog,
    Func<string, RegisteredWork?> getRegisteredWork,
    Func<WorkRequestContext, IWorkSystemSession> createSession,
    WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
    IWorkAuthorizationGroupProvider groupProvider)
{
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowRunState> runs = new();

    public IWorkflowRunHandle Start(
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!catalog.TryGet(workflowName, out var workflow))
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.NotFound(workflowName));
        }

        if (!this.CanOperate(workflow.Definition, requestContext))
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Unauthorized(workflow.Definition.Name));
        }

        if (workflow.Definition.Coordination.IsDurable)
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Invalid(
                [WorkMessage.Error(
                    "workable.workflow.durability.not_supported",
                    $"Workflow '{workflow.Definition.Name}' is marked durable, but durable workflow execution is not available yet.",
                    "workflow.coordination")]));
        }

        var validationMessages = this.ValidateDispatchDurability(workflow);
        if (validationMessages.Count > 0)
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Invalid(validationMessages));
        }

        var run = WorkflowRunState.Create(workflow);
        this.runs[run.Id] = run;
        var completion = this.Execute(run, workflow, requestContext, cancellationToken);
        return WorkflowRunHandle.Accepted(WorkflowStartOutcome.Accepted(run.Id), completion);
    }

    public WorkflowRunSnapshot? Get(WorkflowRunId runId)
        => this.runs.TryGetValue(runId, out var run)
            ? run.ToSnapshot()
            : null;

    private bool CanOperate(WorkflowDefinition definition, WorkRequestContext requestContext)
    {
        if (!requiresAuthorization)
        {
            return true;
        }

        var groups = requestContext.Authorization?.Groups
            ?? groupProvider.GetGroups(requestContext.Actor, systemName)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(systemAuthorizationConfiguration, groups);
        return systemAuthorization.HasOperateAllWorkAccess() ||
            definition.Authorization.CanOperate(groups, requestContext.IsAuthenticated && requestContext.Actor.IsKnown);
    }

    private IReadOnlyList<WorkMessage> ValidateDispatchDurability(RegisteredWorkflow workflow)
    {
        if (workflow.Definition.Coordination.IsDurable)
        {
            return [];
        }

        var messages = new List<WorkMessage>();
        this.ValidateDispatchDurability(workflow.Definition, workflow.Steps, messages);
        return messages;
    }

    private void ValidateDispatchDurability(
        WorkflowDefinition workflow,
        IReadOnlyList<WorkflowStepDefinition> steps,
        List<WorkMessage> messages)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case DispatchWorkflowStepDefinition dispatch:
                    if (getRegisteredWork(dispatch.WorkDefinitionName) is { } registeredWork &&
                        registeredWork.DefaultRuntimePlan.Configuration.Coordination.IsDurabilityEnabled)
                    {
                        messages.Add(WorkMessage.Error(
                            "workable.workflow.child_durability_requires_durable_workflow",
                            $"Workflow '{workflow.Name}' cannot dispatch durably queued work '{dispatch.WorkDefinitionName}' from step '{dispatch.Name}' unless the workflow itself is durable.",
                            "workflow.coordination"));
                    }

                    break;
                case ParallelWorkflowStepDefinition parallel:
                    this.ValidateDispatchDurability(workflow, parallel.Steps, messages);
                    break;
            }
        }
    }

    private async Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var outstanding = new List<(string StepName, IWorkerHandle Handle)>();

        try
        {
            run.MarkRunning();
            var session = createSession(requestContext);

            foreach (var step in workflow.Steps)
            {
                switch (step)
                {
                    case DispatchWorkflowStepDefinition dispatch:
                        {
                            var result = await this.Dispatch(run, session, dispatch, cancellationToken);
                            if (!result.IsAccepted)
                            {
                                return run.Fail(result.Messages);
                            }

                            outstanding.Add((dispatch.Name, result.Handle!));
                            break;
                        }
                    case ParallelWorkflowStepDefinition parallel:
                        {
                            run.MarkStepRunning(parallel.Name);
                            var workerIds = new List<WorkerId>();
                            foreach (var child in parallel.Steps.OfType<DispatchWorkflowStepDefinition>())
                            {
                                var input = AddWorkflowIdentifiers(child.Input, run.Id, run.DefinitionName, child.Name);
                                var handle = await session.Queue.Enqueue(child.WorkDefinitionName, input, cancellationToken: cancellationToken);
                                if (!handle.QueueOutcome.IsAccepted)
                                {
                                    run.FailStep(parallel.Name, handle.QueueOutcome.Messages);
                                    return run.Fail(handle.QueueOutcome.Messages);
                                }

                                if (handle.WorkerId is { } childWorkerId)
                                {
                                    workerIds.Add(childWorkerId);
                                }

                                outstanding.Add((child.Name, handle));
                            }

                            run.MarkStepCompleted(parallel.Name, workerIds);
                            break;
                        }
                    case JoinWorkflowStepDefinition join:
                        {
                            run.MarkStepRunning(join.Name);
                            var completion = await WaitForOutstanding(outstanding, cancellationToken);
                            if (!completion.IsCompletedSuccessfully)
                            {
                                run.FailStep(join.Name, completion.Messages);
                                return run.Fail(completion.Messages);
                            }

                            outstanding.Clear();
                            run.MarkStepCompleted(join.Name);
                            break;
                        }
                    default:
                        return run.Fail(
                            [WorkMessage.Error(
                                "workable.workflow.step.unsupported",
                                $"Workflow step '{step.Name}' uses unsupported kind '{step.Kind}'.",
                                "workflow.step")]);
                }
            }

            if (outstanding.Count > 0)
            {
                var completion = await WaitForOutstanding(outstanding, cancellationToken);
                if (!completion.IsCompletedSuccessfully)
                {
                    return run.Fail(completion.Messages);
                }
            }

            return run.Complete();
        }
        catch (OperationCanceledException)
        {
            return run.Cancel();
        }
        catch (Exception exception)
        {
            return run.Fail(
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")]);
        }
    }

    private async Task<DispatchResult> Dispatch(
        WorkflowRunState run,
        IWorkSystemSession session,
        DispatchWorkflowStepDefinition step,
        CancellationToken cancellationToken)
    {
        run.MarkStepRunning(step.Name);
        var input = AddWorkflowIdentifiers(step.Input, run.Id, run.DefinitionName, step.Name);
        var handle = await session.Queue.Enqueue(step.WorkDefinitionName, input, cancellationToken: cancellationToken);
        if (!handle.QueueOutcome.IsAccepted)
        {
            run.FailStep(step.Name, handle.QueueOutcome.Messages);
            return new DispatchResult(false, null, handle.QueueOutcome.Messages);
        }

        run.MarkStepCompleted(step.Name, handle.WorkerId is { } workerId ? [workerId] : []);
        return new DispatchResult(true, handle, []);
    }

    private static async Task<WorkflowRunCompletion> WaitForOutstanding(
        IReadOnlyList<(string StepName, IWorkerHandle Handle)> outstanding,
        CancellationToken cancellationToken)
    {
        if (outstanding.Count == 0)
        {
            return new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, []);
        }

        var completions = await Task.WhenAll(outstanding.Select(item => item.Handle.WaitForCompletion(cancellationToken)));
        var failure = completions.FirstOrDefault(completion => !completion.IsCompletedSuccessfully);
        return failure is null
            ? new WorkflowRunCompletion(WorkflowRunStatus.Completed, null, [])
            : new WorkflowRunCompletion(ToWorkflowStatus(failure.Status), null, failure.Messages);
    }

    private static WorkflowRunStatus ToWorkflowStatus(WorkCompletionStatus status)
        => status switch
        {
            WorkCompletionStatus.Completed => WorkflowRunStatus.Completed,
            WorkCompletionStatus.Canceled => WorkflowRunStatus.Canceled,
            WorkCompletionStatus.Failed => WorkflowRunStatus.Failed,
            WorkCompletionStatus.Interrupted => WorkflowRunStatus.Failed,
            WorkCompletionStatus.NotFound => WorkflowRunStatus.NotFound,
            WorkCompletionStatus.Invalid => WorkflowRunStatus.Invalid,
            _ => WorkflowRunStatus.Invalid,
        };

    private static WorkInput AddWorkflowIdentifiers(
        WorkInput? input,
        WorkflowRunId runId,
        string workflowDefinitionName,
        string stepName)
        => (input ?? WorkInput.Empty)
            .WithIdentifier(new WorkIdentifier("workflow-run", runId.ToString()))
            .WithIdentifier(new WorkIdentifier("workflow-definition", workflowDefinitionName))
            .WithIdentifier(new WorkIdentifier("workflow-step", stepName));

    private sealed record DispatchResult(
        bool IsAccepted,
        IWorkerHandle? Handle,
        IReadOnlyList<WorkMessage> Messages);

    private sealed class WorkflowRunState
    {
        private readonly Lock sync = new();
        private readonly List<WorkflowStepRunState> steps;
        private IReadOnlyList<WorkMessage> messages = [];
        private WorkflowRunStatus status;
        private DateTimeOffset? startedAt;
        private DateTimeOffset? completedAt;

        private WorkflowRunState(
            WorkflowRunId id,
            string definitionName,
            DateTimeOffset createdAt,
            List<WorkflowStepRunState> steps)
        {
            this.Id = id;
            this.DefinitionName = definitionName;
            this.CreatedAt = createdAt;
            this.steps = steps;
            this.status = WorkflowRunStatus.Running;
        }

        public WorkflowRunId Id { get; }

        public string DefinitionName { get; }

        public DateTimeOffset CreatedAt { get; }

        public static WorkflowRunState Create(RegisteredWorkflow workflow)
            => new(
                WorkflowRunId.New(),
                workflow.Definition.Name,
                DateTimeOffset.UtcNow,
                workflow.Steps.Select(WorkflowStepRunState.FromDefinition).ToList());

        public void MarkRunning()
        {
            lock (this.sync)
            {
                this.startedAt ??= DateTimeOffset.UtcNow;
                this.status = WorkflowRunStatus.Running;
            }
        }

        public void MarkStepRunning(string name)
        {
            lock (this.sync)
            {
                this.steps.Single(step => step.Name == name).MarkRunning();
            }
        }

        public void MarkStepCompleted(string name, IReadOnlyList<WorkerId>? workerIds = null)
        {
            lock (this.sync)
            {
                this.steps.Single(step => step.Name == name).MarkCompleted(workerIds);
            }
        }

        public void FailStep(string name, IReadOnlyList<WorkMessage> stepMessages)
        {
            lock (this.sync)
            {
                this.steps.Single(step => step.Name == name).Fail(stepMessages);
            }
        }

        public WorkflowRunCompletion Complete()
        {
            lock (this.sync)
            {
                this.status = WorkflowRunStatus.Completed;
                this.completedAt = DateTimeOffset.UtcNow;
                return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
            }
        }

        public WorkflowRunCompletion Cancel()
        {
            lock (this.sync)
            {
                this.status = WorkflowRunStatus.Canceled;
                this.completedAt = DateTimeOffset.UtcNow;
                return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
            }
        }

        public WorkflowRunCompletion Fail(IReadOnlyList<WorkMessage> failureMessages)
        {
            lock (this.sync)
            {
                this.status = WorkflowRunStatus.Failed;
                this.messages = failureMessages;
                this.completedAt = DateTimeOffset.UtcNow;
                return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
            }
        }

        public WorkflowRunSnapshot ToSnapshot()
        {
            lock (this.sync)
            {
                return this.ToSnapshotLocked();
            }
        }

        private WorkflowRunSnapshot ToSnapshotLocked()
            => new(
                this.Id,
                this.DefinitionName,
                this.status,
                this.steps.Select(step => step.ToSnapshot()).ToArray(),
                this.CreatedAt,
                this.startedAt,
                this.completedAt,
                this.messages);
    }

    private sealed class WorkflowStepRunState
    {
        private readonly List<WorkerId> workerIds = [];
        private readonly List<WorkflowStepRunState> children = [];
        private IReadOnlyList<WorkMessage> messages = [];

        private WorkflowStepRunState(string name, WorkflowStepKind kind)
        {
            this.Name = name;
            this.Kind = kind;
        }

        public string Name { get; }

        public WorkflowStepKind Kind { get; }

        public WorkflowStepRunStatus Status { get; private set; }

        public DateTimeOffset? StartedAt { get; private set; }

        public DateTimeOffset? CompletedAt { get; private set; }

        public static WorkflowStepRunState FromDefinition(WorkflowStepDefinition definition)
        {
            var state = new WorkflowStepRunState(definition.Name, definition.Kind);
            if (definition is ParallelWorkflowStepDefinition parallel)
            {
                state.children.AddRange(parallel.Steps.Select(FromDefinition));
            }

            return state;
        }

        public void MarkRunning()
        {
            this.Status = WorkflowStepRunStatus.Running;
            this.StartedAt ??= DateTimeOffset.UtcNow;
        }

        public void MarkCompleted(IReadOnlyList<WorkerId>? workerIds = null)
        {
            this.Status = WorkflowStepRunStatus.Completed;
            this.StartedAt ??= DateTimeOffset.UtcNow;
            this.CompletedAt = DateTimeOffset.UtcNow;
            if (workerIds is not null)
            {
                this.workerIds.Clear();
                this.workerIds.AddRange(workerIds);
            }
        }

        public void Fail(IReadOnlyList<WorkMessage> failureMessages)
        {
            this.Status = WorkflowStepRunStatus.Failed;
            this.StartedAt ??= DateTimeOffset.UtcNow;
            this.CompletedAt = DateTimeOffset.UtcNow;
            this.messages = failureMessages;
        }

        public WorkflowStepRunSnapshot ToSnapshot()
            => new(
                this.Name,
                this.Kind,
                this.Status,
                this.workerIds.ToArray(),
                this.StartedAt,
                this.CompletedAt,
                this.messages);
    }
}
