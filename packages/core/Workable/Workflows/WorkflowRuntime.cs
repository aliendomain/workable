using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed class WorkflowRuntime
{
    private static readonly TimeSpan AutoResumeRetryInitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AutoResumeRetryMaximumDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlSessionTimeout = TimeSpan.FromSeconds(30);
    private readonly string? systemName;
    private readonly bool requiresAuthorization;
    private readonly WorkflowCatalog catalog;
    private readonly Func<string, RegisteredWork?> getRegisteredWork;
    private readonly Func<WorkRequestContext, CancellationToken, ValueTask<IWorkSystemSession>> createSession;
    private readonly Func<WorkerId, IWorkerHandle> createWorkerHandle;
    private readonly Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker;
    private readonly WorkflowPersistenceCoordinator persistence;
    private readonly WorkflowEventPublisher workflowEvents;
    private readonly WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration;
    private readonly IWorkAuthorizationGroupResolver groupResolver;
    private readonly ILogger? logger;
    private readonly WorkerOperations? delegatedWorkers;
    private readonly NonDurableWorkflowExecutor nonDurable;
    private readonly DurableWorkflowExecutor? durable;
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowRunState> runs = new();
    private readonly ConcurrentDictionary<WorkflowRunId, Task<WorkflowRunCompletion>> executions = new();
    private readonly ConcurrentDictionary<WorkflowRunId, WorkflowExecutionControl> controls = new();
    private readonly Lock actionGatesSync = new();
    private readonly Dictionary<WorkflowRunId, WorkflowActionGate> actionGates = [];
    private readonly ConcurrentDictionary<WorkflowRunId, byte> cancellationsInProgress = new();
    private readonly ConcurrentDictionary<WorkflowRunId, byte> recoveryPending = new();
    private readonly ConcurrentDictionary<WorkerId, Lazy<Task>> receiptPersistences = new();
    private readonly ConcurrentDictionary<WorkerId, byte> failedReceiptPersistences = new();
    private readonly ConcurrentDictionary<WorkflowRunId, AutoResumeRetryState> autoResumeRetries = new();
    private readonly Lock lifecycleSync = new();
    private CancellationTokenSource executionLifetime = new();
    private long version;

    public WorkflowRuntime(
        string? systemName,
        bool requiresAuthorization,
        WorkflowCatalog catalog,
        Func<string, RegisteredWork?> getRegisteredWork,
        Func<WorkRequestContext, CancellationToken, ValueTask<IWorkSystemSession>> createSession,
        Func<WorkerId, IWorkerHandle> createWorkerHandle,
        Func<WorkerId, CancellationToken, Task<WorkerSnapshot?>>? getAuthoritativeWorker,
        WorkflowPersistenceCoordinator persistence,
        WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
        IWorkAuthorizationGroupResolver groupResolver,
        WorkflowEventPublisher? workflowEvents = null,
        ILogger? logger = null,
        WorkQueueService? delegatedQueue = null,
        WorkerOperations? delegatedWorkers = null)
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
        this.groupResolver = groupResolver;
        this.logger = logger;
        this.delegatedWorkers = delegatedWorkers;
        this.nonDurable = new NonDurableWorkflowExecutor(
            createSession,
            createWorkerHandle,
            this.workflowEvents,
            getAuthoritativeWorker,
            delegatedQueue,
            delegatedWorkers);
        if (!string.IsNullOrWhiteSpace(systemName))
        {
            this.durable = new DurableWorkflowExecutor(
                systemName,
                getRegisteredWork,
                createSession,
                createWorkerHandle,
                persistence,
                this.workflowEvents,
                getAuthoritativeWorker,
                logger,
                delegatedQueue,
                delegatedWorkers);
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

    public async Task StopBackgroundTasks(CancellationToken cancellationToken)
    {
        var pending = this.autoResumeRetries.Values
            .Where(static retry => retry.Task.IsValueCreated)
            .Select(static retry => retry.Task.Value)
            .ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
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
            this.cancellationsInProgress.Clear();
            this.recoveryPending.Clear();
            this.receiptPersistences.Clear();
            this.failedReceiptPersistences.Clear();
            this.autoResumeRetries.Clear();
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
        var recoveredRunIds = await this.LoadDurableRuns(cancellationToken);
        await this.ResumeRecoveredDurableRuns(recoveredRunIds, cancellationToken);
    }

    internal async Task<IReadOnlyList<WorkflowRunId>> LoadDurableRuns(CancellationToken cancellationToken)
    {
        if (!this.persistence.IsAvailable || this.durable is null)
        {
            return [];
        }

        var recoveredRunIds = new List<WorkflowRunId>();
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

            this.recoveryPending[run.Id] = 0;
            recoveredRunIds.Add(run.Id);
            this.AdvanceVersion();
        }

        return recoveredRunIds;
    }

    internal async Task ResumeRecoveredDurableRuns(
        IReadOnlyList<WorkflowRunId> recoveredRunIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveredRunIds);
        var recoveredBlockedRunIds = new List<WorkflowRunId>();
        foreach (var runId in recoveredRunIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!this.runs.TryGetValue(runId, out var run) ||
                !this.catalog.TryGet(run.DefinitionName, out var workflow))
            {
                this.recoveryPending.TryRemove(runId, out _);
                continue;
            }

            this.recoveryPending.TryRemove(runId, out _);
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

    public Task<IWorkflowRunHandle> Start(
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
        => this.Start(
            workflowName,
            requestContext,
            input: null,
            cancellationToken);

    public Task<IWorkflowRunHandle> Start(
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        CancellationToken cancellationToken = default)
        => this.StartCore(workflowName, requestContext, input, cancellationToken);

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

        var operationAuthorization = await this.ResolveOperationAuthorization(
            workflow.Definition,
            requestContext,
            cancellationToken);
        if (!operationAuthorization.IsAllowed)
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
        this.StartExecution(run, workflow, initialAuthorization: operationAuthorization.Authorization);
        return WorkflowRunHandle.Accepted(WorkflowStartOutcome.Accepted(run.Id), run.WaitForCompletion());
    }

    public WorkflowRunSnapshot? Get(WorkflowRunId runId)
        => this.runs.TryGetValue(runId, out var run)
            ? run.ToSnapshot()
            : null;

    internal async ValueTask<WorkflowRunState?> GetVisibleState(
        WorkflowRunId runId,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!this.runs.TryGetValue(runId, out var run))
        {
            return null;
        }

        if (!this.catalog.TryGet(run.DefinitionName, out var workflow) ||
            !await this.CanRead(workflow.Definition, requestContext, cancellationToken))
        {
            return null;
        }

        return run;
    }

    internal async ValueTask<WorkflowRunSnapshot?> GetVisible(
        WorkflowRunId runId,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        return (await this.GetVisibleState(runId, requestContext, cancellationToken))?.ToSnapshot();
    }

    internal async ValueTask<IReadOnlyList<WorkflowRunState>> ListVisibleStates(
        WorkRequestContext requestContext,
        bool includeFinal = false,
        string? definitionName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        IReadOnlySet<string>? groups = null;
        WorkSystemAuthorizationEvaluator? systemAuthorization = null;
        if (this.requiresAuthorization)
        {
            groups = await this.groupResolver.GetGroups(requestContext, this.systemName, cancellationToken);
            systemAuthorization = new WorkSystemAuthorizationEvaluator(this.systemAuthorizationConfiguration, groups);
        }

        var canReadAllWork = !this.requiresAuthorization || systemAuthorization!.HasReadAllWorkAccess();
        var isKnownActor = requestContext.IsAuthenticated && requestContext.Actor.IsKnown;
        var readableDefinitionNames = this.catalog.Definitions
            .Where(workflow => canReadAllWork || workflow.Authorization.CanRead(groups!, isKnownActor))
            .Select(static workflow => workflow.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. this.runs.Values
            .Where(run =>
                (includeFinal || !IsFinal(run.ToSnapshot().Status)) &&
                (string.IsNullOrWhiteSpace(definitionName) || string.Equals(run.DefinitionName, definitionName, StringComparison.OrdinalIgnoreCase)) &&
                readableDefinitionNames.Contains(run.DefinitionName))
            .OrderByDescending(run => run.CreatedAt)];
    }

    internal async ValueTask<IReadOnlyList<WorkflowRunSnapshot>> ListVisible(
        WorkRequestContext requestContext,
        bool includeFinal = false,
        string? definitionName = null,
        CancellationToken cancellationToken = default)
    {
        return [.. (await this.ListVisibleStates(requestContext, includeFinal, definitionName, cancellationToken))
            .Select(run => run.ToSnapshot())];
    }

    public async Task<WorkflowActionOutcome> Execute(
        WorkflowRunId runId,
        WorkflowAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        cancellationToken.ThrowIfCancellationRequested();
        if (action == WorkflowAction.Start &&
            this.runs.TryGetValue(runId, out var settlingRun) &&
            settlingRun.GetStatus() is WorkflowRunStatus.Paused or WorkflowRunStatus.Blocked &&
            this.executions.TryGetValue(runId, out var settlingExecution))
        {
            try
            {
                await settlingExecution.WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (IsNonCriticalExecutionFailure(exception))
            {
                // The action is validated against the authoritative run state after settlement.
            }
        }

        var actionGate = this.ReferenceActionGate(runId);
        await actionGate.Sync.WaitAsync(cancellationToken);
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

        if (!await this.CanOperate(workflow.Definition, requestContext, cancellationToken))
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
                var cancelSession = await this.createSession(requestContext, cancellationToken);
                var childCancellation = await WorkflowExecutionSupport.CancelOutstandingChildren(
                    run,
                    cancelSession,
                    this.getAuthoritativeWorker,
                    cancellationToken,
                    this.delegatedWorkers);
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

        if (control.CancelRequested)
        {
            if (action == WorkflowAction.Cancel)
            {
                return WorkflowActionOutcome.Accepted(action, snapshot);
            }

            return WorkflowActionOutcome.Invalid(
                action,
                runId,
                snapshot,
                [WorkMessage.Error(
                    "workable.workflow.run.cancellation_pending",
                    $"Workflow run '{runId.Value:D}' already has an accepted cancellation and cannot be paused.",
                    "workflow.run")]);
        }

        if (control.PauseRequested)
        {
            if (action == WorkflowAction.Pause)
            {
                return WorkflowActionOutcome.Accepted(action, snapshot);
            }

            return WorkflowActionOutcome.Invalid(
                action,
                runId,
                snapshot,
                [WorkMessage.Error(
                    "workable.workflow.run.pause_pending",
                    $"Workflow run '{runId.Value:D}' already has an accepted pause and cannot be canceled until it settles.",
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
        bool wasPaused = false,
        WorkAuthorizationSnapshot? initialAuthorization = null)
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
            () => this.RunRegisteredExecution(
                run,
                workflow,
                control,
                wasPaused,
                initialAuthorization,
                executionCompletion),
            CancellationToken.None);
    }

    private async Task RunRegisteredExecution(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        WorkflowExecutionControl control,
        bool wasPaused,
        WorkAuthorizationSnapshot? initialAuthorization,
        TaskCompletionSource<WorkflowRunCompletion> executionCompletion)
    {
        WorkflowRunCompletion? result = null;
        OperationCanceledException? cancellation = null;
        Exception? failure = null;
        try
        {
            result = await this.RunExecution(run, workflow, control, wasPaused, initialAuthorization);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }
        catch (Exception exception) when (IsNonCriticalExecutionFailure(exception))
        {
            failure = exception;
        }
        catch (Exception exception) when (!IsNonCriticalExecutionFailure(exception))
        {
            executionCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            if (this.controls.TryRemove(run.Id, out var controlToDispose))
            {
                using var _ = controlToDispose;
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

        if (run.GetStatus() == WorkflowRunStatus.Blocked)
        {
            await this.TryAutoResumeBlockedRunSafely(run.Id);
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
        bool wasPaused = false,
        WorkAuthorizationSnapshot? initialAuthorization = null)
    {
        try
        {
            var completion = await this.Execute(run, workflow, control, wasPaused, initialAuthorization);
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
                    await this.CreateControlSession(
                        control.CancellationRequestContext ?? run.RequestContext),
                    this.getAuthoritativeWorker,
                    CancellationToken.None,
                    this.delegatedWorkers);
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
                var shouldPublishCompletion = !completion.IsFinal ||
                    await this.TryPersistAndSetFinalCompletion(
                        run,
                        completion,
                        shouldPersistState);

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
                        await this.CreateControlSession(
                            control.CancellationRequestContext ?? run.RequestContext),
                        this.getAuthoritativeWorker,
                        CancellationToken.None,
                        this.delegatedWorkers);
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
                await this.CreateControlSession(run.RequestContext),
                this.getAuthoritativeWorker,
                CancellationToken.None,
                this.delegatedWorkers);
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

        if (!TryGetWorkflowProvenance(worker, out var provenance) ||
            !this.runs.TryGetValue(provenance.RunId, out var run) ||
            !string.Equals(provenance.DefinitionName, run.DefinitionName, StringComparison.OrdinalIgnoreCase) ||
            !run.StepContainsWorker(provenance.StepName, worker.Id))
        {
            return;
        }

        await this.TryAutoResumeBlockedRun(provenance.RunId, cancellationToken);
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
        bool wasPaused = false,
        WorkAuthorizationSnapshot? initialAuthorization = null)
        => workflow.Definition.Coordination.IsDurable
            ? this.durable!.Execute(
                run,
                workflow,
                wasPaused,
                () => control.PauseRequested,
                () => control.CancelRequested,
                control.Token,
                initialAuthorization)
            : this.nonDurable.Execute(
                run,
                workflow,
                wasPaused,
                () => control.PauseRequested,
                () => control.CancelRequested,
                control.Token,
                initialAuthorization);

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

    private async Task<AutoResumeAttemptResult> TryAutoResumeBlockedRun(
        WorkflowRunId runId,
        CancellationToken cancellationToken)
    {
        if (!this.runs.TryGetValue(runId, out var run))
        {
            return AutoResumeAttemptResult.NotEligible;
        }

        var snapshot = run.ToSnapshot();
        if (snapshot.Status != WorkflowRunStatus.Blocked ||
            !this.catalog.TryGet(snapshot.DefinitionName, out var workflow))
        {
            return AutoResumeAttemptResult.NotEligible;
        }

        if (this.controls.ContainsKey(runId) ||
            this.executions.ContainsKey(runId) ||
            this.cancellationsInProgress.ContainsKey(runId))
        {
            return AutoResumeAttemptResult.Deferred;
        }

        var outstandingWorkerIds = run.GetOutstandingWorkerIds()
            .Distinct()
            .ToArray();
        if (outstandingWorkerIds.Length == 0)
        {
            return AutoResumeAttemptResult.NotEligible;
        }

        foreach (var workerId in outstandingWorkerIds)
        {
            if (run.TryGetChildReceipt(workerId, out var receipt) &&
                receipt is not null &&
                IsSatisfiedForAutoResume(run, workflow, workerId, receipt.CompletionStatus))
            {
                continue;
            }

            var worker = await this.GetWorkerSnapshot(workerId, cancellationToken);
            if (worker is null ||
                !IsSatisfiedForAutoResume(
                    run,
                    workflow,
                    workerId,
                    WorkerStateMachine.CompletionStatusFor(worker.State)))
            {
                return AutoResumeAttemptResult.NotEligible;
            }
        }

        var actionGate = this.ReferenceActionGate(runId);
        await actionGate.Sync.WaitAsync(CancellationToken.None);
        try
        {
            if (!this.runs.TryGetValue(runId, out var currentRun) ||
                !ReferenceEquals(run, currentRun) ||
                run.GetStatus() != WorkflowRunStatus.Blocked)
            {
                return AutoResumeAttemptResult.NotEligible;
            }

            if (this.controls.ContainsKey(runId) ||
                this.executions.ContainsKey(runId) ||
                this.cancellationsInProgress.ContainsKey(runId))
            {
                return AutoResumeAttemptResult.Deferred;
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
            return AutoResumeAttemptResult.Resumed;
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

        if (!TryGetWorkflowProvenance(worker, out var provenance) ||
            !this.runs.TryGetValue(provenance.RunId, out var run) ||
            !string.Equals(provenance.DefinitionName, run.DefinitionName, StringComparison.OrdinalIgnoreCase) ||
            !run.StepContainsWorker(provenance.StepName, worker.Id))
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
            provenance.StepName,
            worker.DefinitionName,
            worker.State,
            worker.StateChangedAt,
            worker.Messages,
            worker.Output);
        this.catalog.TryGet(run.DefinitionName, out var workflow);
        await this.PersistWorkflowChildReceiptCoalesced(run, provenance.StepName, receipt, workflow);

        if (this.recoveryPending.ContainsKey(run.Id))
        {
            return;
        }

        if (worker.State == WorkerState.Canceled &&
            workflow is not null &&
            WorkflowExecutionSupport.ResolveCanceledChildBehavior(run, workflow, worker.Id) ==
                WorkflowCanceledChildBehavior.CancelWorkflow)
        {
            await this.ApplyCanceledChildWorkflowCancellation(run, workflow, cancellationToken);
        }

        await this.TryAutoResumeBlockedRunSafely(run.Id);
    }

    private async Task TryAutoResumeBlockedRunSafely(WorkflowRunId runId)
    {
        try
        {
            if (await this.TryAutoResumeBlockedRun(runId, CancellationToken.None) ==
                AutoResumeAttemptResult.Deferred)
            {
                this.ScheduleAutoResumeRetry(runId);
            }
        }
        catch (Exception exception) when (IsNonCriticalExecutionFailure(exception))
        {
            this.LogAutoResumeFailure(runId, exception);
            this.ScheduleAutoResumeRetry(runId);
        }
    }

    private void ScheduleAutoResumeRetry(WorkflowRunId runId)
    {
        while (true)
        {
            if (this.autoResumeRetries.TryGetValue(runId, out var existing))
            {
                existing.RequestRetry();
                // Recheck ownership after publishing the request so a retiring retry either
                // consumes it or this caller installs a successor after removal.
                if (this.autoResumeRetries.TryGetValue(runId, out var current) &&
                    ReferenceEquals(existing, current))
                {
                    _ = existing.Task.Value;
                    return;
                }

                continue;
            }

            var created = new AutoResumeRetryState(
                state => this.RetryAutoResumeBlockedRun(runId, state));
            created.RequestRetry();
            if (this.autoResumeRetries.TryAdd(runId, created))
            {
                _ = created.Task.Value;
                return;
            }
        }
    }

    private async Task RetryAutoResumeBlockedRun(
        WorkflowRunId runId,
        AutoResumeRetryState retryState)
    {
        var delay = AutoResumeRetryInitialDelay;
        var cancellationToken = this.GetExecutionLifetimeToken();
        try
        {
            retryState.ConsumeRetryRequest();
            while (this.runs.TryGetValue(runId, out var run) &&
                run.GetStatus() == WorkflowRunStatus.Blocked)
            {
                retryState.ConsumeRetryRequest();
                try
                {
                    await Task.Delay(delay, cancellationToken);
                    var result = await this.TryAutoResumeBlockedRun(runId, cancellationToken);
                    if (result != AutoResumeAttemptResult.Deferred)
                    {
                        return;
                    }

                    delay = NextAutoResumeRetryDelay(delay);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (IsNonCriticalExecutionFailure(exception))
                {
                    this.LogAutoResumeFailure(runId, exception);
                    delay = NextAutoResumeRetryDelay(delay);
                }
            }
        }
        finally
        {
            // Remove this exact owner before consuming the handoff. A racing scheduler will
            // then either leave a request on this state or install the successor itself.
            this.autoResumeRetries.TryRemove(
                new KeyValuePair<WorkflowRunId, AutoResumeRetryState>(runId, retryState));
            if (retryState.ConsumeRetryRequest())
            {
                this.ScheduleAutoResumeRetry(runId);
            }
        }
    }

    private void LogAutoResumeFailure(WorkflowRunId runId, Exception exception)
        => this.logger?.LogWarning(
            exception,
            "Workflow auto-resume processing failed for run {WorkflowRunId} in work system {WorkSystem}; retrying while the run remains blocked.",
            runId.Value,
            this.systemName ?? "default");

    private static TimeSpan NextAutoResumeRetryDelay(TimeSpan delay)
        => TimeSpan.FromMilliseconds(Math.Min(
            delay.TotalMilliseconds * 2,
            AutoResumeRetryMaximumDelay.TotalMilliseconds));

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
                    await this.createSession(run.RequestContext, cancellationToken),
                    this.getAuthoritativeWorker,
                    CancellationToken.None,
                    this.delegatedWorkers);
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

        if (!TryGetWorkflowProvenance(worker, out var provenance) ||
            !this.runs.TryGetValue(provenance.RunId, out var run) ||
            !string.Equals(provenance.DefinitionName, run.DefinitionName, StringComparison.OrdinalIgnoreCase) ||
            !run.StepContainsWorker(provenance.StepName, worker.Id))
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
            TryGetWorkflowProvenance(worker, out var provenance) &&
            this.runs.TryGetValue(provenance.RunId, out var run) &&
            string.Equals(provenance.DefinitionName, run.DefinitionName, StringComparison.OrdinalIgnoreCase) &&
            run.StepContainsWorker(provenance.StepName, worker.Id);
    }

    internal async Task ObservePurgedWorkflowChild(
        WorkerSnapshot worker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);

        if (!TryGetWorkflowProvenance(worker, out var provenance) ||
            !this.runs.TryGetValue(provenance.RunId, out var run) ||
            !string.Equals(provenance.DefinitionName, run.DefinitionName, StringComparison.OrdinalIgnoreCase) ||
            !run.StepContainsWorker(provenance.StepName, worker.Id) ||
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
        var worker = await this.GetWorkerSnapshot(workerId, cancellationToken);
        return worker?.State == WorkerState.Completed
            ? worker
            : null;
    }

    private async Task<WorkerSnapshot?> GetWorkerSnapshot(
        WorkerId workerId,
        CancellationToken cancellationToken)
    {
        var worker = this.getAuthoritativeWorker is not null
            ? await this.getAuthoritativeWorker(workerId, cancellationToken)
            : await (await this.createSession(
                WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                cancellationToken)).Query.Worker(workerId, cancellationToken);
        return worker;
    }

    private static bool IsSatisfiedForAutoResume(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        WorkerId workerId,
        WorkCompletionStatus completionStatus)
        => completionStatus == WorkCompletionStatus.Completed ||
            (completionStatus == WorkCompletionStatus.Canceled &&
                WorkflowExecutionSupport.ResolveCanceledChildBehavior(run, workflow, workerId) ==
                    WorkflowCanceledChildBehavior.Continue);

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
                session = await this.createSession(
                    WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                    cancellationToken);
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

    private static bool TryGetWorkflowProvenance(
        WorkerSnapshot worker,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkflowProvenance? provenance)
    {
        provenance = worker.WorkflowProvenance;
        if (provenance is null ||
            string.IsNullOrWhiteSpace(provenance.DefinitionName) ||
            string.IsNullOrWhiteSpace(provenance.StepName))
        {
            provenance = null;
            return false;
        }

        return true;
    }

    private async ValueTask<bool> CanOperate(
        WorkflowDefinition definition,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
        => (await this.ResolveOperationAuthorization(definition, requestContext, cancellationToken)).IsAllowed;

    private async ValueTask<(bool IsAllowed, WorkAuthorizationSnapshot? Authorization)> ResolveOperationAuthorization(
        WorkflowDefinition definition,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!this.requiresAuthorization)
        {
            return (true, null);
        }

        var groups = await this.groupResolver.GetGroups(requestContext, this.systemName, cancellationToken);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(this.systemAuthorizationConfiguration, groups);
        var isAllowed = systemAuthorization.HasOperateAllWorkAccess() ||
            definition.Authorization.CanOperate(groups, requestContext.IsAuthenticated && requestContext.Actor.IsKnown);
        var authorization = requestContext.Authorization is { Scope: { } scope } snapshot &&
            snapshot.Actor == requestContext.Actor &&
            scope.IsForSystem(this.systemName)
            ? snapshot
            : WorkAuthorizationSnapshot.CreateForSystem(
                this.systemName,
                requestContext.Actor,
                groups,
                readableDefinitionIds: null,
                isAuthenticated: requestContext.IsAuthenticated);
        return (isAllowed, authorization);
    }

    private async ValueTask<IWorkSystemSession> CreateControlSession(WorkRequestContext requestContext)
    {
        using var timeout = new CancellationTokenSource(ControlSessionTimeout);
        return await this.createSession(requestContext, timeout.Token).AsTask().WaitAsync(timeout.Token);
    }

    private async ValueTask<bool> CanRead(
        WorkflowDefinition definition,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!this.requiresAuthorization)
        {
            return true;
        }

        var groups = await this.groupResolver.GetGroups(requestContext, this.systemName, cancellationToken);
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

    private enum AutoResumeAttemptResult
    {
        NotEligible,
        Deferred,
        Resumed,
    }

    private sealed class AutoResumeRetryState
    {
        private int retryRequested;

        public AutoResumeRetryState(Func<AutoResumeRetryState, Task> createTask)
        {
            this.Task = new Lazy<Task>(
                () => createTask(this),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Lazy<Task> Task { get; }

        public void RequestRetry()
            => Interlocked.Exchange(ref this.retryRequested, 1);

        public bool ConsumeRetryRequest()
            => Interlocked.Exchange(ref this.retryRequested, 0) == 1;
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
