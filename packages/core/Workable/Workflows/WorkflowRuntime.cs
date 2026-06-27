using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkflowRuntime
{
    private readonly string? systemName;
    private readonly bool requiresAuthorization;
    private readonly WorkflowCatalog catalog;
    private readonly Func<string, RegisteredWork?> getRegisteredWork;
    private readonly Func<WorkRequestContext, IWorkSystemSession> createSession;
    private readonly WorkflowPersistenceCoordinator persistence;
    private readonly WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration;
    private readonly IWorkAuthorizationGroupProvider groupProvider;
    private readonly NonDurableWorkflowExecutor nonDurable;
    private readonly DurableWorkflowExecutor? durable;
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowRunState> runs = new();
    private readonly ConcurrentDictionary<WorkflowRunId, Task<WorkflowRunCompletion>> executions = new();
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowExecutionControl> controls = new();
    private readonly Lock lifecycleSync = new();
    private CancellationTokenSource executionLifetime = new();

    public WorkflowRuntime(
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
        this.createSession = createSession;
        this.persistence = persistence;
        this.systemAuthorizationConfiguration = systemAuthorizationConfiguration;
        this.groupProvider = groupProvider;
        this.nonDurable = new NonDurableWorkflowExecutor(createSession);
        if (!string.IsNullOrWhiteSpace(systemName))
        {
            this.durable = new DurableWorkflowExecutor(
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
        List<WorkflowExecutionControl> activeControls;
        lock (this.lifecycleSync)
        {
            activeRuns = [.. this.runs.Values];
            activeControls = [.. this.controls.Values];
            this.runs.Clear();
            this.executions.Clear();
            this.controls.Clear();
        }

        foreach (var run in activeRuns)
        {
            run.TrySetCompletion(new WorkflowRunCompletion(WorkflowRunStatus.Canceled, null, []));
        }

        foreach (var control in activeControls)
        {
            control.Dispose();
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

    internal WorkflowRunState? GetVisibleState(
        WorkflowRunId runId,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!this.runs.TryGetValue(runId, out var run))
        {
            return null;
        }

        if (!this.catalog.TryGet(run.DefinitionName, out var workflow) ||
            !this.CanRead(workflow.Definition, requestContext))
        {
            return null;
        }

        return run;
    }

    internal WorkflowRunSnapshot? GetVisible(
        WorkflowRunId runId,
        WorkRequestContext requestContext)
    {
        return this.GetVisibleState(runId, requestContext)?.ToSnapshot();
    }

    internal IReadOnlyList<WorkflowRunState> ListVisibleStates(
        WorkRequestContext requestContext,
        bool includeFinal = false,
        string? definitionName = null)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        return [.. this.runs.Values
            .Where(run =>
                (includeFinal || run.ToSnapshot().Status == WorkflowRunStatus.Running) &&
                (string.IsNullOrWhiteSpace(definitionName) || string.Equals(run.DefinitionName, definitionName, StringComparison.OrdinalIgnoreCase)) &&
                this.catalog.TryGet(run.DefinitionName, out var workflow) &&
                this.CanRead(workflow.Definition, requestContext))
            .OrderByDescending(run => run.CreatedAt)];
    }

    internal IReadOnlyList<WorkflowRunSnapshot> ListVisible(
        WorkRequestContext requestContext,
        bool includeFinal = false,
        string? definitionName = null)
    {
        return [.. this.ListVisibleStates(requestContext, includeFinal, definitionName)
            .Select(run => run.ToSnapshot())];
    }

    public async Task<WorkflowActionOutcome> Execute(
        WorkflowRunId runId,
        WorkflowAction action,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!this.runs.TryGetValue(runId, out var run))
        {
            return WorkflowActionOutcome.NotFound(action, runId);
        }

        var snapshot = run.ToSnapshot();
        if (snapshot.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled)
        {
            return WorkflowActionOutcome.Invalid(
                action,
                runId,
                snapshot,
                [WorkMessage.Error(
                    "workable.workflow.run.final",
                    $"Workflow run '{runId.Value:D}' is already final and cannot accept '{action}'.",
                    "workflow.run")]);
        }

        if (!this.catalog.TryGet(snapshot.DefinitionName, out var workflow))
        {
            return WorkflowActionOutcome.Invalid(
                action,
                runId,
                snapshot,
                [WorkMessage.Error(
                    "workable.workflow.definition.not_found",
                    $"Workflow definition '{snapshot.DefinitionName}' is no longer registered.",
                    "workflow.definition")]);
        }

        if (!this.CanOperate(workflow.Definition, requestContext))
        {
            return WorkflowActionOutcome.Unauthorized(action, runId);
        }

        if (!this.controls.TryGetValue(runId, out var control))
        {
            return WorkflowActionOutcome.Invalid(
                action,
                runId,
                snapshot,
                [WorkMessage.Error(
                    "workable.workflow.run.not_executing",
                    $"Workflow run '{runId.Value:D}' is not currently executing.",
                    "workflow.run")]);
        }

        var outcomeSnapshot = run.ToSnapshot();
        if (workflow.Definition.Coordination.IsDurable)
        {
            if (!run.TryRecordAcceptedControlAction(action, out outcomeSnapshot))
            {
                return WorkflowActionOutcome.Invalid(
                    action,
                    runId,
                    outcomeSnapshot,
                    [WorkMessage.Error(
                        "workable.workflow.run.final",
                        $"Workflow run '{runId.Value:D}' is already final and cannot accept '{action}'.",
                        "workflow.run")]);
            }

            await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
        }

        if (action == WorkflowAction.Cancel)
        {
            control.RequestCancel();
        }
        else
        {
            control.RequestStop();
        }

        return WorkflowActionOutcome.Accepted(action, outcomeSnapshot);
    }

    private void StartExecution(WorkflowRunState run, RegisteredWorkflow workflow)
    {
        if (this.executions.ContainsKey(run.Id))
        {
            throw new InvalidOperationException(
                $"Workflow run '{run.Id.Value:D}' is already executing.");
        }

        var control = new WorkflowExecutionControl(this.GetExecutionLifetimeToken());
        if (run.GetPendingControlAction() is { } pendingAction)
        {
            ApplyControlRequest(control, pendingAction);
        }

        if (!this.controls.TryAdd(run.Id, control))
        {
            control.Dispose();
            throw new InvalidOperationException(
                $"Workflow run '{run.Id.Value:D}' is already executing.");
        }

        var executionTask = Task.Run(
            () => this.RunExecution(run, workflow, control),
            CancellationToken.None);
        if (!this.executions.TryAdd(run.Id, executionTask))
        {
            this.controls.TryRemove(run.Id, out var removedControl);
            removedControl?.Dispose();
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
        WorkflowExecutionControl control)
    {
        try
        {
            var completion = await this.Execute(run, workflow, control);
            if (control.CancelRequested && completion.Status == WorkflowRunStatus.Canceled)
            {
                await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    this.createSession(run.RequestContext),
                    CancellationToken.None);
                if (workflow.Definition.Coordination.IsDurable)
                {
                    await this.persistence.DeleteRun(run.Id, CancellationToken.None);
                }
            }

            if (control.StopRequested &&
                completion.Status == WorkflowRunStatus.Canceled &&
                workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.DeleteRun(run.Id, CancellationToken.None);
            }

            run.TrySetCompletion(completion);
            return completion;
        }
        catch (OperationCanceledException) when (control.Token.IsCancellationRequested)
        {
            var completion = run.Cancel();
            if (control.CancelRequested)
            {
                await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    this.createSession(run.RequestContext),
                    CancellationToken.None);
                if (workflow.Definition.Coordination.IsDurable)
                {
                    await this.persistence.DeleteRun(run.Id, CancellationToken.None);
                }
            }

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
            if (this.controls.TryRemove(run.Id, out var controlToDispose))
            {
                controlToDispose.Dispose();
            }
        }
    }

    private Task<WorkflowRunCompletion> RunExecution(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
        => RunExecutionWithEphemeralControl(this, run, workflow, cancellationToken);

    static async Task<WorkflowRunCompletion> RunExecutionWithEphemeralControl(
        WorkflowRuntime runtime,
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
    {
        using var control = new WorkflowExecutionControl(cancellationToken);
        return await runtime.RunExecution(run, workflow, control);
    }

    private Task<WorkflowRunCompletion> Execute(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        WorkflowExecutionControl control)
        => workflow.Definition.Coordination.IsDurable
            ? this.durable!.Execute(
                run,
                workflow,
                control.ShouldStopBeforeStep,
                () => control.StopRequested,
                control.Token)
            : this.nonDurable.Execute(
                run,
                workflow,
                control.ShouldStopBeforeStep,
                () => control.StopRequested,
                control.Token);

    private WorkflowRunPersistenceRecord CreatePersistenceRecord(WorkflowRunState run)
        => run.ToPersistenceRecord(this.systemName);

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

    private bool CanRead(WorkflowDefinition definition, WorkRequestContext requestContext)
    {
        if (!this.requiresAuthorization)
        {
            return true;
        }

        var groups = requestContext.Authorization?.Groups
            ?? this.groupProvider.GetGroups(requestContext.Actor, this.systemName)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(this.systemAuthorizationConfiguration, groups);
        return systemAuthorization.HasReadAllWorkAccess() ||
            definition.Authorization.CanRead(groups, requestContext.IsAuthenticated && requestContext.Actor.IsKnown);
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

    private sealed class WorkflowExecutionControl : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private int stopRequested;
        private int cancelRequested;

        public WorkflowExecutionControl(CancellationToken lifetimeToken)
        {
            this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        }

        public CancellationToken Token => this.cancellation.Token;

        public bool StopRequested => Volatile.Read(ref this.stopRequested) == 1;

        public bool CancelRequested => Volatile.Read(ref this.cancelRequested) == 1;

        public void RequestStop()
        {
            Interlocked.Exchange(ref this.stopRequested, 1);
        }

        public void RequestCancel()
        {
            Interlocked.Exchange(ref this.cancelRequested, 1);
            this.cancellation.Cancel();
        }

        public bool ShouldStopBeforeStep(WorkflowStepDefinition step)
            => this.StopRequested &&
                step is not JoinWorkflowStepDefinition;

        public void Dispose()
        {
            this.cancellation.Dispose();
        }
    }

    private static void ApplyControlRequest(WorkflowExecutionControl control, WorkflowAction action)
    {
        if (action == WorkflowAction.Cancel)
        {
            control.RequestCancel();
            return;
        }

        control.RequestStop();
    }
}
