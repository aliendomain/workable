using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkflowRuntime
{
    private readonly string? systemName;
    private readonly bool requiresAuthorization;
    private readonly WorkflowCatalog catalog;
    private readonly Func<string, RegisteredWork?> getRegisteredWork;
    private readonly Func<WorkRequestContext, IWorkSystemSession> createSession;
    private readonly Func<WorkerId, IWorkerHandle> createWorkerHandle;
    private readonly Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker;
    private readonly WorkflowPersistenceCoordinator persistence;
    private readonly WorkflowEventPublisher workflowEvents;
    private readonly WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration;
    private readonly IWorkAuthorizationGroupProvider groupProvider;
    private readonly NonDurableWorkflowExecutor nonDurable;
    private readonly DurableWorkflowExecutor? durable;
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowRunState> runs = new();
    private readonly ConcurrentDictionary<WorkflowRunId, Task<WorkflowRunCompletion>> executions = new();
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowExecutionControl> controls = new();
    private readonly Lock lifecycleSync = new();
    private CancellationTokenSource executionLifetime = new();
    private long version;

    public WorkflowRuntime(
        string? systemName,
        bool requiresAuthorization,
        WorkflowCatalog catalog,
        Func<string, RegisteredWork?> getRegisteredWork,
        Func<WorkRequestContext, IWorkSystemSession> createSession,
        Func<WorkerId, IWorkerHandle> createWorkerHandle,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker,
        WorkflowPersistenceCoordinator persistence,
        WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
        IWorkAuthorizationGroupProvider groupProvider,
        WorkflowEventPublisher? workflowEvents = null)
    {
        this.systemName = systemName;
        this.requiresAuthorization = requiresAuthorization;
        this.catalog = catalog;
        this.getRegisteredWork = getRegisteredWork;
        this.createSession = createSession;
        this.createWorkerHandle = createWorkerHandle;
        this.getAuthoritativeWorker = getAuthoritativeWorker;
        this.persistence = persistence;
        this.workflowEvents = workflowEvents ?? new WorkflowEventPublisher(default, null, new WorkEventStream());
        this.systemAuthorizationConfiguration = systemAuthorizationConfiguration;
        this.groupProvider = groupProvider;
        this.nonDurable = new NonDurableWorkflowExecutor(createSession, createWorkerHandle, this.workflowEvents, getAuthoritativeWorker);
        if (!string.IsNullOrWhiteSpace(systemName))
        {
            this.durable = new DurableWorkflowExecutor(
                systemName,
                getRegisteredWork,
                createSession,
                createWorkerHandle,
                persistence,
                this.workflowEvents,
                getAuthoritativeWorker);
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

    public long Version => Interlocked.Read(ref this.version);

    public void CancelExecutionLifetime()
    {
        lock (this.lifecycleSync)
        {
            this.executionLifetime.Cancel();
        }
    }

    public Task StopBackgroundTasks(CancellationToken cancellationToken)
        => Task.CompletedTask;

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

        var recoveredBlockedRunIds = new List<WorkflowRunId>();
        await foreach (var record in this.persistence.ListRuns(cancellationToken))
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

            var run = WorkflowRunState.Rehydrate(workflow, record, this.AdvanceVersion);
            if (!this.runs.TryAdd(run.Id, run))
            {
                continue;
            }

            this.AdvanceVersion();
            if (IsFinal(run.GetStatus()))
            {
                await this.TryPurgeFinalRunIfChildrenGone(run, cancellationToken);
            }
            else if (ShouldCancelWorkflowForCanceledChild(run, workflow))
            {
                await this.ApplyCanceledChildWorkflowCancellation(run, workflow, cancellationToken);
            }
            else if (run.GetStatus() == WorkflowRunStatus.Running)
            {
                this.StartExecution(run, workflow);
            }
            else if (run.GetStatus() == WorkflowRunStatus.Blocked)
            {
                recoveredBlockedRunIds.Add(run.Id);
            }
        }

        foreach (var runId in recoveredBlockedRunIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await this.TryAutoResumeBlockedRun(runId, cancellationToken);
        }
    }

    public IWorkflowRunHandle Start(
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
        => this.Start(
            workflowName,
            requestContext,
            input: null,
            cancellationToken);

    public IWorkflowRunHandle Start(
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        CancellationToken cancellationToken = default)
        => this.StartCore(workflowName, requestContext, input, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    private async Task<IWorkflowRunHandle> StartCore(
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        CancellationToken cancellationToken)
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
        var run = WorkflowRunState.Create(workflow, requestContext.WithoutAuthorization(), input, this.AdvanceVersion);
        if (workflow.Definition.Coordination.IsDurable)
        {
            await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), cancellationToken);
        }

        this.runs[run.Id] = run;
        this.AdvanceVersion();
        this.workflowEvents.Started(run.ToSnapshot(), requestContext);
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
                (includeFinal || !IsFinal(run.ToSnapshot().Status)) &&
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
        if (IsFinal(snapshot.Status))
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

        if (action == WorkflowAction.Start)
        {
            if (snapshot.Status is not (WorkflowRunStatus.Paused or WorkflowRunStatus.Blocked))
            {
                return WorkflowActionOutcome.Invalid(
                    action,
                    runId,
                    snapshot,
                    [WorkMessage.Error(
                        "workable.workflow.run.not_resumable",
                        $"Workflow run '{runId.Value:D}' cannot be started from status '{snapshot.Status}'.",
                        "workflow.run")]);
            }

            if (this.controls.ContainsKey(runId) || this.executions.ContainsKey(runId))
            {
                return WorkflowActionOutcome.Invalid(
                    action,
                    runId,
                    snapshot,
                    [WorkMessage.Error(
                        "workable.workflow.run.executing",
                        $"Workflow run '{runId.Value:D}' is already executing.",
                        "workflow.run")]);
            }

            var wasPaused = snapshot.Status == WorkflowRunStatus.Paused;
            run.MarkRunning();
            var resumedSnapshot = run.ToSnapshot();
            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            }

            this.StartExecution(run, workflow, wasPaused);
            this.workflowEvents.ActionAccepted(resumedSnapshot, action, requestContext);
            return WorkflowActionOutcome.Accepted(action, resumedSnapshot);
        }

        if (snapshot.Status is WorkflowRunStatus.Paused or WorkflowRunStatus.Blocked)
        {
            if (action != WorkflowAction.Cancel)
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

            var cancelSession = this.createSession(run.RequestContext);
            await WorkflowExecutionSupport.CancelOutstandingChildren(run, cancelSession, this.getAuthoritativeWorker, CancellationToken.None);
            var canceled = run.Cancel();
            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            }

            run.TrySetCompletion(canceled);
            this.workflowEvents.ActionAccepted(canceled.Run ?? snapshot, action, requestContext);
            this.workflowEvents.Completion(canceled);
            await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
            return WorkflowActionOutcome.Accepted(action, canceled.Run ?? snapshot);
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
            control.RequestPause();
        }

        this.workflowEvents.ActionAccepted(outcomeSnapshot, action, requestContext);
        return WorkflowActionOutcome.Accepted(action, outcomeSnapshot);
    }

    private void StartExecution(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        bool wasPaused = false)
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
            () => this.RunExecution(run, workflow, control, wasPaused),
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
        var run = WorkflowRunState.FromPersistenceRecord(record, this.AdvanceVersion);
        var completion = run.Fail(
            [WorkMessage.Error(
                "workable.workflow.definition_mismatch",
                $"Workflow '{record.DefinitionName}' run '{record.RunId.Value:D}' could not be recovered because the persisted workflow definition fingerprint does not match the current registered workflow.",
                "workflow.definition")]);
        run.TrySetCompletion(completion);
        this.workflowEvents.Completion(completion);
        this.runs.TryAdd(run.Id, run);
        this.AdvanceVersion();
        await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
        await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
    }

    private async Task<WorkflowRunCompletion> RunExecution(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        WorkflowExecutionControl control,
        bool wasPaused = false)
    {
        try
        {
            var completion = await this.Execute(run, workflow, control, wasPaused);
            if (completion.Status == WorkflowRunStatus.Canceled &&
                (control.CancelRequested || completion.CancelOutstandingChildren))
            {
                await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    this.createSession(run.RequestContext),
                    this.getAuthoritativeWorker,
                    CancellationToken.None);
            }

            var shouldPersistFinalState = workflow.Definition.Coordination.IsDurable &&
                ShouldPersistFinalState(completion, control);
            if (completion.IsFinal)
            {
                run.TrySetCompletion(completion);
                if (shouldPersistFinalState)
                {
                    await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
                }
            }

            this.workflowEvents.Completion(completion);
            if (completion.IsFinal && shouldPersistFinalState)
            {
                await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
            }

            return completion;
        }
        catch (OperationCanceledException) when (control.Token.IsCancellationRequested)
        {
            if (control.CancelRequested || !control.PauseRequested)
            {
                var completion = run.Cancel();
                if (control.CancelRequested)
                {
                    await WorkflowExecutionSupport.CancelOutstandingChildren(
                        run,
                        this.createSession(run.RequestContext),
                        this.getAuthoritativeWorker,
                        CancellationToken.None);
                }

                run.TrySetCompletion(completion);
                if (workflow.Definition.Coordination.IsDurable &&
                    ShouldPersistFinalState(completion, control))
                {
                    await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
                }

                this.workflowEvents.Completion(completion);
                if (ShouldPersistFinalState(completion, control))
                {
                    await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
                }

                return completion;
            }

            await WorkflowExecutionSupport.PauseOutstandingChildren(
                run,
                this.createSession(run.RequestContext),
                this.getAuthoritativeWorker,
                CancellationToken.None);
            var paused = run.Pause();
            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            }

            this.workflowEvents.Completion(paused);
            return paused;
        }
        catch (Exception exception)
        {
            var completion = run.Fail(
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")]);
            run.TrySetCompletion(completion);
            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
            }

            this.workflowEvents.Completion(completion);
            await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
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

    internal async Task TryAutoResumeBlockedRunForCompletedWorker(
        WorkerId workerId,
        CancellationToken cancellationToken)
    {
        var worker = await this.GetCompletedWorkerSnapshot(workerId, cancellationToken);
        if (worker is null)
        {
            return;
        }

        var workflowRunIdentifier = worker.Identifiers.FirstOrDefault(identifier => identifier.Type == "workflow-run");
        if (string.IsNullOrWhiteSpace(workflowRunIdentifier.Value) ||
            !Guid.TryParse(workflowRunIdentifier.Value, out var workflowRunId))
        {
            return;
        }

        await this.TryAutoResumeBlockedRun(new WorkflowRunId(workflowRunId), cancellationToken);
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
        WorkflowExecutionControl control,
        bool wasPaused = false)
        => workflow.Definition.Coordination.IsDurable
            ? this.durable!.Execute(
                run,
                workflow,
                wasPaused,
                () => control.PauseRequested,
                () => control.CancelRequested,
                control.Token)
            : this.nonDurable.Execute(
                run,
                workflow,
                wasPaused,
                () => control.PauseRequested,
                () => control.CancelRequested,
                control.Token);

    private WorkflowRunPersistenceRecord CreatePersistenceRecord(WorkflowRunState run)
        => run.ToPersistenceRecord(this.systemName);

    private async Task TryAutoResumeBlockedRun(
        WorkflowRunId runId,
        CancellationToken cancellationToken)
    {
        if (!this.runs.TryGetValue(runId, out var run))
        {
            return;
        }

        var snapshot = run.ToSnapshot();
        if (snapshot.Status != WorkflowRunStatus.Blocked ||
            this.controls.ContainsKey(runId) ||
            this.executions.ContainsKey(runId) ||
            !this.catalog.TryGet(snapshot.DefinitionName, out var workflow))
        {
            return;
        }

        var outstandingWorkerIds = run.GetOutstandingWorkerIds()
            .Distinct()
            .ToArray();
        if (outstandingWorkerIds.Length == 0)
        {
            return;
        }

        foreach (var workerId in outstandingWorkerIds)
        {
            if (run.TryGetChildReceipt(workerId, out var receipt) &&
                receipt?.CompletionStatus == WorkCompletionStatus.Completed)
            {
                continue;
            }

            var worker = await this.GetCompletedWorkerSnapshot(workerId, cancellationToken);
            if (worker is null)
            {
                return;
            }
        }

        run.MarkRunning();
        var resumedSnapshot = run.ToSnapshot();
        if (workflow.Definition.Coordination.IsDurable)
        {
            await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
        }

        try
        {
            this.StartExecution(run, workflow, wasPaused: false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        this.workflowEvents.ActionAccepted(resumedSnapshot, WorkflowAction.Start, run.RequestContext);
    }

    private CancellationToken GetExecutionLifetimeToken()
    {
        lock (this.lifecycleSync)
        {
            return this.executionLifetime.Token;
        }
    }

    private void AdvanceVersion()
        => Interlocked.Increment(ref this.version);

    internal async Task ObserveFinalWorkflowChild(
        WorkerSnapshot worker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);

        if (!TryGetWorkflowIdentifiers(worker, out var runId, out var stepName) ||
            !this.runs.TryGetValue(runId, out var run))
        {
            return;
        }

        var completionStatus = WorkerStateMachine.CompletionStatusFor(worker.State);
        if (completionStatus == WorkCompletionStatus.Invalid)
        {
            return;
        }

        var receipt = new WorkflowChildReceipt(
            worker.Id,
            stepName,
            worker.DefinitionName,
            worker.State,
            worker.StateChangedAt,
            worker.Messages,
            worker.Output);
        if (!run.RecordChildReceipt(receipt))
        {
            return;
        }

        this.catalog.TryGet(run.DefinitionName, out var workflow);
        if (workflow?.Definition.Coordination.IsDurable == true)
        {
            await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), cancellationToken);
        }

        this.workflowEvents.StepUpdated(run.ToSnapshot(), stepName);
        if (worker.State == WorkerState.Canceled &&
            workflow is not null &&
            WorkflowExecutionSupport.ResolveCanceledChildBehavior(run, workflow, worker.Id) ==
                WorkflowCanceledChildBehavior.CancelWorkflow)
        {
            await this.ApplyCanceledChildWorkflowCancellation(run, workflow, cancellationToken);
        }
    }

    private async Task ApplyCanceledChildWorkflowCancellation(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
    {
        if (this.controls.TryGetValue(run.Id, out var control))
        {
            control.RequestCancel();
            return;
        }

        var status = run.GetStatus();
        if (status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Blocked or WorkflowRunStatus.Paused) ||
            (status == WorkflowRunStatus.Running && this.executions.ContainsKey(run.Id)))
        {
            return;
        }

        await WorkflowExecutionSupport.CancelOutstandingChildren(
            run,
            this.createSession(run.RequestContext),
            this.getAuthoritativeWorker,
            CancellationToken.None);
        var canceled = run.Cancel();
        if (workflow.Definition.Coordination.IsDurable)
        {
            await this.persistence.UpsertRun(this.CreatePersistenceRecord(run), CancellationToken.None);
        }

        run.TrySetCompletion(canceled);
        this.workflowEvents.Completion(canceled);
        await this.TryPurgeFinalRunIfChildrenGone(run, cancellationToken);
    }

    private static bool ShouldCancelWorkflowForCanceledChild(
        WorkflowRunState run,
        RegisteredWorkflow workflow)
        => run.GetChildReceipts().Any(receipt =>
            receipt.CompletionStatus == WorkCompletionStatus.Canceled &&
            WorkflowExecutionSupport.ResolveCanceledChildBehavior(run, workflow, receipt.WorkerId) ==
                WorkflowCanceledChildBehavior.CancelWorkflow);

    internal bool ShouldKeepWorkflowChildWorker(WorkerSnapshot worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        if (!TryGetWorkflowIdentifiers(worker, out var runId, out _) ||
            !this.runs.TryGetValue(runId, out var run))
        {
            return false;
        }

        var status = run.GetStatus();
        if (IsFinal(status))
        {
            return false;
        }

        return !run.TryGetChildReceipt(worker.Id, out var receipt) ||
            receipt?.CompletionStatus != WorkCompletionStatus.Completed;
    }

    internal async Task ObservePurgedWorkflowChild(
        WorkerSnapshot worker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);

        if (!TryGetWorkflowIdentifiers(worker, out var runId, out _) ||
            !this.runs.TryGetValue(runId, out var run) ||
            !IsFinal(run.GetStatus()))
        {
            return;
        }

        await this.TryPurgeFinalRunIfChildrenGone(run, cancellationToken, worker.Id);
    }

    private async Task<WorkerSnapshot?> GetCompletedWorkerSnapshot(
        WorkerId workerId,
        CancellationToken cancellationToken)
    {
        var worker = this.getAuthoritativeWorker is not null
            ? await this.getAuthoritativeWorker(workerId, cancellationToken)
            : await this.createSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess)).Query.Worker(workerId, cancellationToken);
        return worker?.State == WorkerState.Completed
            ? worker
            : null;
    }

    private static bool IsFinal(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;

    private async Task TryPurgeFinalRunIfChildrenGone(
        WorkflowRunState run,
        CancellationToken cancellationToken,
        WorkerId? justPurgedWorkerId = null)
    {
        if (!IsFinal(run.GetStatus()))
        {
            return;
        }

        var workerIds = run.GetAllWorkerIds();
        IWorkSystemSession? session = null;
        if (this.getAuthoritativeWorker is null)
        {
            try
            {
                session = this.createSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
            }
            catch (NotSupportedException)
            {
                return;
            }
        }

        foreach (var workerId in workerIds)
        {
            var snapshot = this.getAuthoritativeWorker is not null
                ? await this.getAuthoritativeWorker(workerId, cancellationToken)
                : await session!.Query.Worker(workerId, cancellationToken);
            if (snapshot is not null)
            {
                return;
            }

            if (justPurgedWorkerId != workerId &&
                await this.persistence.DurableWorkerExists(workerId, cancellationToken))
            {
                return;
            }
        }

        if (this.runs.TryRemove(run.Id, out _))
        {
            this.AdvanceVersion();
        }
        if (this.catalog.TryGet(run.DefinitionName, out var workflow) &&
            workflow.Definition.Coordination.IsDurable)
        {
            await this.persistence.DeleteRun(run.Id, cancellationToken);
        }
    }

    private static bool ShouldPersistFinalState(
        WorkflowRunCompletion completion,
        WorkflowExecutionControl control)
        => completion.Status switch
        {
            WorkflowRunStatus.Canceled => control.CancelRequested || completion.CancelOutstandingChildren,
            WorkflowRunStatus.Completed => true,
            WorkflowRunStatus.Failed => true,
            _ => false,
        };

    private static bool TryGetWorkflowIdentifiers(
        WorkerSnapshot worker,
        out WorkflowRunId runId,
        out string stepName)
    {
        var workflowRunIdentifier = worker.Identifiers.FirstOrDefault(identifier => identifier.Type == "workflow-run");
        var workflowStepIdentifier = worker.Identifiers.FirstOrDefault(identifier => identifier.Type == "workflow-step");
        if (!Guid.TryParse(workflowRunIdentifier.Value, out var parsedRunId) ||
            string.IsNullOrWhiteSpace(workflowStepIdentifier.Value))
        {
            runId = default;
            stepName = string.Empty;
            return false;
        }

        runId = new WorkflowRunId(parsedRunId);
        stepName = workflowStepIdentifier.Value;
        return true;
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
                    if (this.getRegisteredWork(dispatch.WorkDefinition.Name) is { } registeredWork &&
                        registeredWork.DefaultRuntimePlan.Configuration.Coordination.IsDurabilityEnabled)
                    {
                        messages.Add(WorkMessage.Error(
                            "workable.workflow.child_durability_requires_durable_workflow",
                            $"Workflow '{workflow.Name}' cannot dispatch durably queued work '{dispatch.WorkDefinition.Name}' from step '{dispatch.Name}' unless the workflow itself is durable.",
                            "workflow.coordination"));
                    }

                    break;
                case ParallelWorkflowStepDefinition parallel:
                    this.ValidateDispatchDurability(workflow, parallel.Steps, messages);
                    break;
                case BranchWorkflowStepDefinition branch:
                    this.ValidateDispatchDurability(workflow, branch.Steps, messages);
                    break;
            }
        }
    }

    private sealed class WorkflowExecutionControl : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private int pauseRequested;
        private int cancelRequested;

        public WorkflowExecutionControl(CancellationToken lifetimeToken)
        {
            this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        }

        public CancellationToken Token => this.cancellation.Token;

        public bool PauseRequested => Volatile.Read(ref this.pauseRequested) == 1;

        public bool CancelRequested => Volatile.Read(ref this.cancelRequested) == 1;

        public void RequestPause()
        {
            Interlocked.Exchange(ref this.pauseRequested, 1);
            this.cancellation.Cancel();
        }

        public void RequestCancel()
        {
            Interlocked.Exchange(ref this.cancelRequested, 1);
            this.cancellation.Cancel();
        }

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

        control.RequestPause();
    }
}
