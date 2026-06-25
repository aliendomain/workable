using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkflowRuntime
{
    private readonly string? systemName;
    private readonly bool requiresAuthorization;
    private readonly WorkflowCatalog catalog;
    private readonly Func<string, RegisteredWork?> getRegisteredWork;
    private readonly WorkflowPersistenceCoordinator persistence;
    private readonly WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration;
    private readonly IWorkAuthorizationGroupProvider groupProvider;
    private readonly NonDurableWorkflowExecutor nonDurable;
    private readonly DurableWorkflowExecutor? durable;
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowRunState> runs = new();
    private readonly ConcurrentDictionary<WorkflowRunId, Task<WorkflowRunCompletion>> executions = new();
    private readonly Lock lifecycleSync = new();
    private CancellationTokenSource executionLifetime = new();

    public WorkflowRuntime(
        WorkSystemId workSystemId,
        string? systemName,
        bool requiresAuthorization,
        WorkflowCatalog catalog,
        Func<string, RegisteredWork?> getRegisteredWork,
        Func<WorkRequestContext, IWorkSystemSession> createSession,
        Func<WorkerId, IWorkerHandle> createWorkerHandle,
        WorkflowPersistenceCoordinator persistence,
        WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
        IWorkAuthorizationGroupProvider groupProvider)
    {
        this.systemName = systemName;
        this.requiresAuthorization = requiresAuthorization;
        this.catalog = catalog;
        this.getRegisteredWork = getRegisteredWork;
        this.persistence = persistence;
        this.systemAuthorizationConfiguration = systemAuthorizationConfiguration;
        this.groupProvider = groupProvider;
        this.nonDurable = new NonDurableWorkflowExecutor(createSession);
        if (!string.IsNullOrWhiteSpace(systemName))
        {
            this.durable = new DurableWorkflowExecutor(
                workSystemId,
                systemName,
                getRegisteredWork,
                createSession,
                createWorkerHandle,
                persistence);
        }
    }

    public void StartExecutionLifetime()
    {
        lock (this.lifecycleSync)
        {
            if (!this.executionLifetime.IsCancellationRequested)
            {
                return;
            }

            this.executionLifetime.Dispose();
            this.executionLifetime = new CancellationTokenSource();
        }
    }

    public void CancelExecutionLifetime()
    {
        lock (this.lifecycleSync)
        {
            this.executionLifetime.Cancel();
        }
    }

    public void ClearRuns()
    {
        List<WorkflowRunState> activeRuns;
        lock (this.lifecycleSync)
        {
            activeRuns = [.. this.runs.Values];
            this.runs.Clear();
            this.executions.Clear();
        }

        foreach (var run in activeRuns)
        {
            run.TrySetCompletion(new WorkflowRunCompletion(WorkflowRunStatus.Canceled, null, []));
        }
    }

    public async Task WaitForExecutions(CancellationToken cancellationToken)
    {
        Task<WorkflowRunCompletion>[] pending;
        lock (this.lifecycleSync)
        {
            pending = [.. this.executions.Values];
        }

        if (pending.Length == 0)
        {
            return;
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    public async Task RecoverDurableRuns(CancellationToken cancellationToken)
    {
        if (!this.persistence.IsAvailable || this.durable is null)
        {
            return;
        }

        await foreach (var record in this.persistence.ListIncompleteRuns(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!this.catalog.TryGet(record.DefinitionName, out var workflow) ||
                !workflow.Definition.Coordination.IsDurable ||
                !string.Equals(record.WorkSystemName, this.systemName, StringComparison.Ordinal))
            {
                continue;
            }

            var currentFingerprint = WorkflowDefinitionFingerprint.Create(workflow);
            if (!string.Equals(record.DefinitionFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                await this.FailRecoveredRunForDefinitionMismatch(record);
                continue;
            }

            var run = WorkflowRunState.Rehydrate(workflow, record);
            if (!this.runs.TryAdd(run.Id, run))
            {
                continue;
            }

            this.StartExecution(run, workflow);
        }
    }

    public IWorkflowRunHandle Start(
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentNullException.ThrowIfNull(requestContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (!this.catalog.TryGet(workflowName, out var workflow))
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.NotFound(workflowName));
        }

        if (!this.CanOperate(workflow.Definition, requestContext))
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Unauthorized(workflow.Definition.Name));
        }

        if (workflow.Definition.Coordination.IsDurable)
        {
            if (!this.persistence.IsAvailable)
            {
                return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Invalid(
                    [WorkMessage.Error(
                        "workable.workflow.coordination.persistence_store_required",
                        $"Workflow '{workflow.Definition.Name}' is marked durable, but no work persistence store is registered.",
                        "workflow.coordination")]));
            }

            if (this.durable is null)
            {
                return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Invalid(
                    [WorkMessage.Error(
                        "workable.workflow.coordination.named_system_required",
                        $"Workflow '{workflow.Definition.Name}' is marked durable, but durable workflows require a named Workable system.",
                        "workflow.coordination")]));
            }
        }

        var validationMessages = this.ValidateDispatchDurability(workflow);
        if (validationMessages.Count > 0)
        {
            return WorkflowRunHandle.Rejected(WorkflowStartOutcome.Invalid(validationMessages));
        }

        // Workable stores origin and actor durably, but not precomputed authorization snapshots.
        var run = WorkflowRunState.Create(workflow, requestContext.WithoutAuthorization());
        this.runs[run.Id] = run;
        this.StartExecution(run, workflow);
        return WorkflowRunHandle.Accepted(WorkflowStartOutcome.Accepted(run.Id), run.WaitForCompletion());
    }

    public WorkflowRunSnapshot? Get(WorkflowRunId runId)
        => this.runs.TryGetValue(runId, out var run)
            ? run.ToSnapshot()
            : null;

    private void StartExecution(WorkflowRunState run, RegisteredWorkflow workflow)
    {
        var executionTask = Task.Run(
            () => this.RunExecution(run, workflow, this.GetExecutionLifetimeToken()),
            CancellationToken.None);
        if (!this.executions.TryAdd(run.Id, executionTask))
        {
            throw new InvalidOperationException(
                $"Workflow run '{run.Id.Value:D}' is already executing.");
        }
    }

    private async Task FailRecoveredRunForDefinitionMismatch(WorkflowRunPersistenceRecord record)
    {
        var run = WorkflowRunState.FromPersistenceRecord(record);
        var completion = run.Fail(
            [WorkMessage.Error(
                "workable.workflow.definition_mismatch",
                $"Workflow '{record.DefinitionName}' run '{record.RunId.Value:D}' could not be recovered because the persisted workflow definition fingerprint does not match the current registered workflow.",
                "workflow.definition")]);
        run.TrySetCompletion(completion);
        this.runs.TryAdd(run.Id, run);
        await this.persistence.DeleteRun(record.RunId, CancellationToken.None);
    }

    private async Task<WorkflowRunCompletion> RunExecution(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
    {
        try
        {
            var completion = await this.Execute(run, workflow, cancellationToken);
            run.TrySetCompletion(completion);
            return completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var completion = run.Cancel();
            run.TrySetCompletion(completion);
            return completion;
        }
        catch (Exception exception)
        {
            var completion = run.Fail(
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")]);
            run.TrySetCompletion(completion);
            return completion;
        }
        finally
        {
            this.executions.TryRemove(run.Id, out _);
        }
    }

    private Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
        => workflow.Definition.Coordination.IsDurable
            ? this.durable!.Execute(run, workflow, cancellationToken)
            : this.nonDurable.Execute(run, workflow, cancellationToken);

    private CancellationToken GetExecutionLifetimeToken()
    {
        lock (this.lifecycleSync)
        {
            return this.executionLifetime.Token;
        }
    }

    private bool CanOperate(WorkflowDefinition definition, WorkRequestContext requestContext)
    {
        if (!this.requiresAuthorization)
        {
            return true;
        }

        var groups = requestContext.Authorization?.Groups
            ?? this.groupProvider.GetGroups(requestContext.Actor, this.systemName)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(this.systemAuthorizationConfiguration, groups);
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
                    if (this.getRegisteredWork(dispatch.WorkDefinitionName) is { } registeredWork &&
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
}
