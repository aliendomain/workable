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
    private readonly Lock actionGatesSync = new();
    private readonly Dictionary<WorkflowRunId, WorkflowActionGate> actionGates = [];
    private readonly ConcurrentDictionary<WorkflowRunId, byte> cancellationsInProgress = new();
    private readonly ConcurrentDictionary<WorkerId, Lazy<Task>> receiptPersistences = new();
    private readonly ConcurrentDictionary<WorkerId, byte> failedReceiptPersistences = new();
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
            this.cancellationsInProgress.Clear();
            this.receiptPersistences.Clear();
            this.failedReceiptPersistences.Clear();
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
            await this.persistence.UpsertRun(
                run.Id,
                () => this.CreatePersistenceRecord(run),
                cancellationToken);
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
        if (action == WorkflowAction.Start &&
            this.runs.TryGetValue(runId, out var settlingRun) &&
            settlingRun.GetStatus() is WorkflowRunStatus.Paused or WorkflowRunStatus.Blocked &&
            this.executions.TryGetValue(runId, out var settlingExecution))
        {
            try
            {
                await settlingExecution;
            }
            catch (Exception exception) when (IsNonCriticalExecutionFailure(exception))
            {
                // The action is validated against the authoritative run state after settlement.
            }
        }

        var actionGate = this.ReferenceActionGate(runId);
        await actionGate.Sync.WaitAsync(CancellationToken.None);
        try
        {

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
            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRunAndApply(
                    run.Id,
                    () => run.ToRunningPersistenceRecord(this.systemName),
                    run.MarkRunning,
                    CancellationToken.None);
            }
            else
            {
                run.MarkRunning();
            }
            var resumedSnapshot = run.ToSnapshot();
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

            this.cancellationsInProgress[runId] = 0;

            try
            {
                var cancelSession = this.createSession(requestContext);
                var childCancellation = await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    cancelSession,
                    this.getAuthoritativeWorker,
                    CancellationToken.None);
                if (!childCancellation.IsSuccessful)
                {
                    return WorkflowActionOutcome.Invalid(action, runId, run.ToSnapshot(), childCancellation.Messages);
                }

                var canceled = run.CreateFinalCompletion(WorkflowRunStatus.Canceled);
                if (await this.TryPersistAndSetFinalCompletion(
                    run,
                    canceled,
                    workflow.Definition.Coordination.IsDurable))
                {
                    this.workflowEvents.ActionAccepted(canceled.Run ?? snapshot, action, requestContext);
                    this.workflowEvents.Completion(canceled);
                    await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
                }

                return WorkflowActionOutcome.Accepted(action, canceled.Run ?? snapshot);
            }
            finally
            {
                this.cancellationsInProgress.TryRemove(runId, out _);
            }
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
        var storedActionContext = requestContext.WithoutAuthorization();
        if (workflow.Definition.Coordination.IsDurable)
        {
            await this.persistence.UpsertRunAndApply(
                run.Id,
                () => run.ToPendingControlActionPersistenceRecord(this.systemName, action, storedActionContext),
                () =>
                {
                    if (!run.TryRecordAcceptedControlAction(action, storedActionContext, out outcomeSnapshot))
                    {
                        throw new InvalidOperationException(
                            $"Workflow run '{run.Id.Value:D}' became final while its '{action}' action gate was held.");
                    }
                },
                CancellationToken.None);
        }

        if (action == WorkflowAction.Cancel)
        {
            control.RequestCancelWithContext(storedActionContext);
        }
        else
        {
            control.RequestPause();
        }

        this.workflowEvents.ActionAccepted(outcomeSnapshot, action, requestContext);
        return WorkflowActionOutcome.Accepted(action, outcomeSnapshot);
        }
        finally
        {
            actionGate.Sync.Release();
            this.ReleaseActionGate(runId, actionGate);
        }
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
            ApplyControlRequest(control, pendingAction, run.GetPendingControlRequestContext());
        }

        if (!this.controls.TryAdd(run.Id, control))
        {
            control.Dispose();
            throw new InvalidOperationException(
                $"Workflow run '{run.Id.Value:D}' is already executing.");
        }

        var executionCompletion = new TaskCompletionSource<WorkflowRunCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!this.executions.TryAdd(run.Id, executionCompletion.Task))
        {
            this.controls.TryRemove(run.Id, out var removedControl);
            removedControl?.Dispose();
            throw new InvalidOperationException(
                $"Workflow run '{run.Id.Value:D}' is already executing.");
        }

        _ = Task.Run(
            () => this.RunRegisteredExecution(run, workflow, control, wasPaused, executionCompletion),
            CancellationToken.None);
    }

    private async Task RunRegisteredExecution(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        WorkflowExecutionControl control,
        bool wasPaused,
        TaskCompletionSource<WorkflowRunCompletion> executionCompletion)
    {
        WorkflowRunCompletion? result = null;
        OperationCanceledException? cancellation = null;
        Exception? failure = null;
        try
        {
            result = await this.RunExecution(run, workflow, control, wasPaused);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (this.controls.TryRemove(run.Id, out var controlToDispose))
            {
                controlToDispose.Dispose();
            }

            this.executions.TryRemove(run.Id, out _);
        }

        if (result is not null)
        {
            executionCompletion.TrySetResult(result);
        }
        else if (cancellation is not null)
        {
            executionCompletion.TrySetCanceled(cancellation.CancellationToken);
        }
        else
        {
            executionCompletion.TrySetException(failure!);
        }
    }

    private async Task FailRecoveredRunForDefinitionMismatch(WorkflowRunPersistenceRecord record)
    {
        var run = WorkflowRunState.FromPersistenceRecord(record, this.AdvanceVersion);
        var completion = run.CreateFinalCompletion(
            WorkflowRunStatus.Failed,
            [WorkMessage.Error(
                "workable.workflow.definition_mismatch",
                $"Workflow '{record.DefinitionName}' run '{record.RunId.Value:D}' could not be recovered because the persisted workflow definition fingerprint does not match the current registered workflow.",
                "workflow.definition")]);
        this.runs.TryAdd(run.Id, run);
        this.AdvanceVersion();
        if (await this.TryPersistAndSetFinalCompletionWithActionGate(run, completion, shouldPersist: true))
        {
            this.workflowEvents.Completion(completion);
            await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
        }
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
            if (control.CancelRequested && !completion.IsFinal)
            {
                completion = new WorkflowRunCompletion(
                    WorkflowRunStatus.Canceled,
                    null,
                    completion.Messages,
                    CancelOutstandingChildren: true);
            }

            if (completion.Status == WorkflowRunStatus.Canceled &&
                (control.CancelRequested || completion.CancelOutstandingChildren))
            {
                var childCancellation = await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    this.createSession(control.CancellationRequestContext ?? run.RequestContext),
                    this.getAuthoritativeWorker,
                    CancellationToken.None);
                if (!childCancellation.IsSuccessful)
                {
                    completion = run.Block(childCancellation.Messages);
                }
            }

            var actionGate = this.ReferenceActionGate(run.Id);
            await actionGate.Sync.WaitAsync(CancellationToken.None);
            try
            {
                if (!completion.IsFinal && run.GetStatus() != completion.Status)
                {
                    var current = run.ToSnapshot();
                    return new WorkflowRunCompletion(current.Status, current, current.Messages);
                }

                if (completion.Status == WorkflowRunStatus.Canceled)
                {
                    completion = run.CreateFinalCompletion(
                        WorkflowRunStatus.Canceled,
                        completion.Messages,
                        completion.CancelOutstandingChildren);
                }

                var shouldPersistState = workflow.Definition.Coordination.IsDurable &&
                    (completion.Status == WorkflowRunStatus.Blocked || ShouldPersistFinalState(completion, control));
                var shouldPublishCompletion = completion.IsFinal
                    ? await this.TryPersistAndSetFinalCompletion(
                        run,
                        completion,
                        shouldPersistState)
                    : true;

                if (completion.IsFinal && !shouldPublishCompletion)
                {
                    return await run.WaitForCompletion();
                }

                if (!completion.IsFinal && shouldPersistState)
                {
                    await this.persistence.UpsertRun(run.Id, () => this.CreatePersistenceRecord(run), CancellationToken.None);
                }

                if (shouldPublishCompletion)
                {
                    this.workflowEvents.Completion(completion);
                }

                if (completion.IsFinal && shouldPersistState && shouldPublishCompletion)
                {
                    await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
                }

                return completion;
            }
            finally
            {
                actionGate.Sync.Release();
                this.ReleaseActionGate(run.Id, actionGate);
            }
        }
        catch (OperationCanceledException) when (control.Token.IsCancellationRequested)
        {
            if (control.CancelRequested || !control.PauseRequested)
            {
                if (control.CancelRequested)
                {
                    var childCancellation = await WorkflowExecutionSupport.CancelOutstandingChildren(
                        run,
                        this.createSession(control.CancellationRequestContext ?? run.RequestContext),
                        this.getAuthoritativeWorker,
                        CancellationToken.None);
                    if (!childCancellation.IsSuccessful)
                    {
                        var blocked = run.Block(childCancellation.Messages);
                        if (workflow.Definition.Coordination.IsDurable)
                        {
                            await this.persistence.UpsertRun(run.Id, () => this.CreatePersistenceRecord(run), CancellationToken.None);
                        }

                        this.workflowEvents.Completion(blocked);
                        return blocked;
                    }
                }

                var completion = run.CreateFinalCompletion(WorkflowRunStatus.Canceled);
                var shouldPersistCompletion = workflow.Definition.Coordination.IsDurable &&
                    ShouldPersistFinalState(completion, control);
                var shouldPublishCompletion = await this.TryPersistAndSetFinalCompletionWithActionGate(
                    run,
                    completion,
                    shouldPersistCompletion);

                if (shouldPublishCompletion)
                {
                    this.workflowEvents.Completion(completion);
                    if (ShouldPersistFinalState(completion, control))
                    {
                        await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
                    }
                }

                return shouldPublishCompletion
                    ? completion
                    : await run.WaitForCompletion();
            }

            await WorkflowExecutionSupport.PauseOutstandingChildren(
                run,
                this.createSession(run.RequestContext),
                this.getAuthoritativeWorker,
                CancellationToken.None);
            var paused = run.Pause();
            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRun(run.Id, () => this.CreatePersistenceRecord(run), CancellationToken.None);
            }

            this.workflowEvents.Completion(paused);
            return paused;
        }
        catch (Exception exception) when (!run.IsCompletionFaulted)
        {
            var completion = run.CreateFinalCompletion(
                WorkflowRunStatus.Failed,
                [WorkMessage.Error(
                    "workable.workflow.execution_exception",
                    exception.Message,
                    "workflow.execution")]);
            var shouldPublishCompletion = await this.TryPersistAndSetFinalCompletionWithActionGate(
                run,
                completion,
                workflow.Definition.Coordination.IsDurable);

            if (shouldPublishCompletion)
            {
                this.workflowEvents.Completion(completion);
                await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
            }
            return shouldPublishCompletion
                ? completion
                : await run.WaitForCompletion();
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

        if (!TryGetWorkflowIdentifiers(worker, out var runId, out var stepName) ||
            !this.runs.TryGetValue(runId, out var run) ||
            !run.StepContainsWorker(stepName, worker.Id))
        {
            return;
        }

        await this.TryAutoResumeBlockedRun(runId, cancellationToken);
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

    private async Task<bool> TryPersistAndSetFinalCompletion(
        WorkflowRunState run,
        WorkflowRunCompletion completion,
        bool shouldPersist)
    {
        if (!run.TryClaimCompletion())
        {
            return false;
        }

        try
        {
            if (shouldPersist)
            {
                await this.persistence.UpsertRunAndApply(
                    run.Id,
                    () => run.ToPersistenceRecord(this.systemName, completion),
                    () =>
                    {
                        var persistedCompletion = run.CommitFinalCompletion(completion);
                        run.TrySetClaimedCompletion(persistedCompletion);
                    },
                    CancellationToken.None);
                return true;
            }
        }
        catch (Exception exception)
        {
            run.TrySetClaimedCompletionException(exception);
            throw;
        }

        var committed = run.CommitFinalCompletion(completion);
        run.TrySetClaimedCompletion(committed);
        return true;
    }

    private async Task<bool> TryPersistAndSetFinalCompletionWithActionGate(
        WorkflowRunState run,
        WorkflowRunCompletion completion,
        bool shouldPersist)
    {
        var actionGate = this.ReferenceActionGate(run.Id);
        await actionGate.Sync.WaitAsync(CancellationToken.None);
        try
        {
            return await this.TryPersistAndSetFinalCompletion(run, completion, shouldPersist);
        }
        finally
        {
            actionGate.Sync.Release();
            this.ReleaseActionGate(run.Id, actionGate);
        }
    }

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
            this.cancellationsInProgress.ContainsKey(runId) ||
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

        var actionGate = this.ReferenceActionGate(runId);
        await actionGate.Sync.WaitAsync(CancellationToken.None);
        try
        {
            if (!this.runs.TryGetValue(runId, out var currentRun) ||
                !ReferenceEquals(run, currentRun) ||
                run.GetStatus() != WorkflowRunStatus.Blocked ||
                this.controls.ContainsKey(runId) ||
                this.executions.ContainsKey(runId) ||
                this.cancellationsInProgress.ContainsKey(runId))
            {
                return;
            }

            if (workflow.Definition.Coordination.IsDurable)
            {
                await this.persistence.UpsertRunAndApply(
                    run.Id,
                    () => run.ToRunningPersistenceRecord(this.systemName),
                    run.MarkRunning,
                    CancellationToken.None);
            }
            else
            {
                run.MarkRunning();
            }
            var resumedSnapshot = run.ToSnapshot();
            this.StartExecution(run, workflow, wasPaused: false);
            this.workflowEvents.ActionAccepted(resumedSnapshot, WorkflowAction.Start, run.RequestContext);
        }
        finally
        {
            actionGate.Sync.Release();
            this.ReleaseActionGate(runId, actionGate);
        }
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
            !this.runs.TryGetValue(runId, out var run) ||
            !run.StepContainsWorker(stepName, worker.Id))
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
        this.catalog.TryGet(run.DefinitionName, out var workflow);
        await this.PersistWorkflowChildReceiptCoalesced(run, stepName, receipt, workflow);

        if (worker.State == WorkerState.Canceled &&
            workflow is not null &&
            WorkflowExecutionSupport.ResolveCanceledChildBehavior(run, workflow, worker.Id) ==
                WorkflowCanceledChildBehavior.CancelWorkflow)
        {
            await this.ApplyCanceledChildWorkflowCancellation(run, workflow, cancellationToken);
        }
    }

    private async Task PersistWorkflowChildReceiptCoalesced(
        WorkflowRunState run,
        string stepName,
        WorkflowChildReceipt receipt,
        RegisteredWorkflow? workflow)
    {
        while (true)
        {
            var receiptPersistence = this.receiptPersistences.GetOrAdd(
                receipt.WorkerId,
                _ => new Lazy<Task>(
                    () => this.PersistWorkflowChildReceipt(run, stepName, receipt, workflow),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                await receiptPersistence.Value;
            }
            finally
            {
                if (this.receiptPersistences.TryGetValue(receipt.WorkerId, out var current) &&
                    ReferenceEquals(current, receiptPersistence))
                {
                    this.receiptPersistences.TryRemove(receipt.WorkerId, out _);
                }
            }

            if (run.TryGetChildReceipt(receipt.WorkerId, out var persistedReceipt) &&
                (persistedReceipt == receipt || persistedReceipt?.CompletedAt >= receipt.CompletedAt))
            {
                return;
            }
        }
    }

    private async Task PersistWorkflowChildReceipt(
        WorkflowRunState run,
        string stepName,
        WorkflowChildReceipt receipt,
        RegisteredWorkflow? workflow)
    {
        var isRetry = this.failedReceiptPersistences.ContainsKey(receipt.WorkerId);
        var changed = run.RecordChildReceipt(receipt);
        try
        {
            if (workflow?.Definition.Coordination.IsDurable == true)
            {
                await this.persistence.UpsertRunCoalesced(
                    run.Id,
                    () => this.CreatePersistenceRecord(run),
                    CancellationToken.None);
            }

            this.failedReceiptPersistences.TryRemove(receipt.WorkerId, out _);
        }
        catch
        {
            this.failedReceiptPersistences[receipt.WorkerId] = 0;
            throw;
        }

        if (changed || isRetry)
        {
            this.workflowEvents.StepUpdated(run.ToSnapshot(), stepName);
        }
    }

    private async Task ApplyCanceledChildWorkflowCancellation(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        CancellationToken cancellationToken)
    {
        if (run.GetStatus() == WorkflowRunStatus.Running &&
            this.controls.TryGetValue(run.Id, out var control))
        {
            control.RequestCancel();
        }

        if (this.cancellationsInProgress.ContainsKey(run.Id))
        {
            return;
        }

        var actionGate = this.ReferenceActionGate(run.Id);
        await actionGate.Sync.WaitAsync(CancellationToken.None);
        try
        {
            var status = run.GetStatus();
            if (status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Blocked or WorkflowRunStatus.Paused) ||
                !this.cancellationsInProgress.TryAdd(run.Id, 0))
            {
                return;
            }

            try
            {
                var childCancellation = await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    this.createSession(run.RequestContext),
                    this.getAuthoritativeWorker,
                    CancellationToken.None);
                if (!childCancellation.IsSuccessful)
                {
                    var blocked = run.Block(childCancellation.Messages);
                    if (workflow.Definition.Coordination.IsDurable)
                    {
                        await this.persistence.UpsertRun(run.Id, () => this.CreatePersistenceRecord(run), CancellationToken.None);
                    }

                    this.workflowEvents.Completion(blocked);
                    return;
                }

                var canceled = run.CreateFinalCompletion(WorkflowRunStatus.Canceled);
                if (await this.TryPersistAndSetFinalCompletion(
                    run,
                    canceled,
                    workflow.Definition.Coordination.IsDurable))
                {
                    this.workflowEvents.Completion(canceled);
                    await this.TryPurgeFinalRunIfChildrenGone(run, CancellationToken.None);
                }
            }
            finally
            {
                this.cancellationsInProgress.TryRemove(run.Id, out _);
            }
        }
        finally
        {
            actionGate.Sync.Release();
            this.ReleaseActionGate(run.Id, actionGate);
        }
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

        if (!TryGetWorkflowIdentifiers(worker, out var runId, out var stepName) ||
            !this.runs.TryGetValue(runId, out var run) ||
            !run.StepContainsWorker(stepName, worker.Id))
        {
            return false;
        }

        if (this.failedReceiptPersistences.ContainsKey(worker.Id) ||
            this.receiptPersistences.ContainsKey(worker.Id))
        {
            return true;
        }

        var status = run.GetStatus();
        if (IsFinal(status))
        {
            return false;
        }

        return !run.TryGetChildReceipt(worker.Id, out var receipt) ||
            receipt?.CompletionStatus != WorkCompletionStatus.Completed;
    }

    internal bool ShouldRetryWorkflowChildFinalization(WorkerSnapshot worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        return this.failedReceiptPersistences.ContainsKey(worker.Id) &&
            TryGetWorkflowIdentifiers(worker, out var runId, out var stepName) &&
            this.runs.TryGetValue(runId, out var run) &&
            run.StepContainsWorker(stepName, worker.Id);
    }

    internal async Task ObservePurgedWorkflowChild(
        WorkerSnapshot worker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);

        if (!TryGetWorkflowIdentifiers(worker, out var runId, out var stepName) ||
            !this.runs.TryGetValue(runId, out var run) ||
            !run.StepContainsWorker(stepName, worker.Id) ||
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

    private static bool IsNonCriticalExecutionFailure(Exception exception)
        => exception is not (
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            ThreadAbortException or
            InvalidProgramException);

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

        var durableWorkerIds = new List<WorkerId>(workerIds.Count);
        foreach (var workerId in workerIds)
        {
            var snapshot = this.getAuthoritativeWorker is not null
                ? await this.getAuthoritativeWorker(workerId, cancellationToken)
                : await session!.Query.Worker(workerId, cancellationToken);
            if (snapshot is not null)
            {
                return;
            }

            if (justPurgedWorkerId != workerId)
            {
                durableWorkerIds.Add(workerId);
            }
        }

        if (durableWorkerIds.Count > 0 &&
            (await this.persistence.DurableWorkersExist(durableWorkerIds, cancellationToken)).Count > 0)
        {
            return;
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

    private WorkflowActionGate ReferenceActionGate(WorkflowRunId runId)
    {
        lock (this.actionGatesSync)
        {
            if (!this.actionGates.TryGetValue(runId, out var gate))
            {
                gate = new WorkflowActionGate();
                this.actionGates[runId] = gate;
            }

            gate.References++;
            return gate;
        }
    }

    private void ReleaseActionGate(WorkflowRunId runId, WorkflowActionGate gate)
    {
        lock (this.actionGatesSync)
        {
            gate.References--;
            if (gate.References == 0)
            {
                this.actionGates.Remove(runId);
            }
        }
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
        private WorkRequestContext? cancellationRequestContext;

        public WorkflowExecutionControl(CancellationToken lifetimeToken)
        {
            this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        }

        public CancellationToken Token => this.cancellation.Token;

        public bool PauseRequested => Volatile.Read(ref this.pauseRequested) == 1;

        public bool CancelRequested => Volatile.Read(ref this.cancelRequested) == 1;

        public WorkRequestContext? CancellationRequestContext => Volatile.Read(ref this.cancellationRequestContext);

        public void RequestPause()
        {
            Interlocked.Exchange(ref this.pauseRequested, 1);
            this.cancellation.Cancel();
        }

        public void RequestCancel()
            => this.RequestCancelCore(requestContext: null);

        public void RequestCancelWithContext(WorkRequestContext requestContext)
        {
            ArgumentNullException.ThrowIfNull(requestContext);
            this.RequestCancelCore(requestContext);
        }

        private void RequestCancelCore(WorkRequestContext? requestContext)
        {
            if (requestContext is not null)
            {
                Volatile.Write(ref this.cancellationRequestContext, requestContext.WithoutAuthorization());
            }

            Interlocked.Exchange(ref this.cancelRequested, 1);
            this.cancellation.Cancel();
        }

        public void Dispose()
        {
            this.cancellation.Dispose();
        }
    }

    private sealed class WorkflowActionGate
    {
        public SemaphoreSlim Sync { get; } = new(1, 1);

        public int References { get; set; }
    }

    private static void ApplyControlRequest(
        WorkflowExecutionControl control,
        WorkflowAction action,
        WorkRequestContext? requestContext)
    {
        if (action == WorkflowAction.Cancel)
        {
            if (requestContext is null)
            {
                control.RequestCancel();
            }
            else
            {
                control.RequestCancelWithContext(requestContext);
            }

            return;
        }

        control.RequestPause();
    }
}
