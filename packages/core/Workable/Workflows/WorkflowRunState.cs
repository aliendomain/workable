using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class WorkflowRunState
{
    private readonly Lock sync = new();
    private readonly List<WorkflowStepRunState> steps;
    private readonly Dictionary<WorkerId, WorkflowChildReceipt> childReceipts = [];
    private readonly Dictionary<WorkerId, HashSet<string>> workerStepNames = [];
    private readonly Action? onChanged;
    private readonly TaskCompletionSource<WorkflowRunCompletion> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int completionClaimed;
    private IReadOnlyList<WorkMessage> messages = [];
    private WorkflowRunStatus status;
    private WorkflowAction? pendingControlAction;
    private WorkRequestContext? pendingControlRequestContext;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? completedAt;

    private WorkflowRunState(
        WorkflowRunId id,
        WorkflowDefinitionVersion definitionVersion,
        string definitionName,
        string definitionFingerprint,
        WorkInput? input,
        WorkRequestContext requestContext,
        DateTimeOffset createdAt,
        List<WorkflowStepRunState> steps,
        Action? onChanged = null)
    {
        this.Id = id;
        this.DefinitionVersion = definitionVersion;
        this.DefinitionName = definitionName;
        this.DefinitionFingerprint = definitionFingerprint;
        this.Input = input;
        this.RequestContext = requestContext;
        this.CreatedAt = createdAt;
        this.steps = steps;
        this.RebuildWorkerStepIndexLocked();
        this.onChanged = onChanged;
        this.status = WorkflowRunStatus.Running;
    }

    public WorkflowRunId Id { get; }

    public WorkflowDefinitionVersion DefinitionVersion { get; }

    public string DefinitionName { get; }

    public string DefinitionFingerprint { get; }

    public WorkInput? Input { get; }

    public WorkRequestContext RequestContext { get; }

    public DateTimeOffset CreatedAt { get; }

    public static WorkflowRunState Create(
        RegisteredWorkflow workflow,
        WorkRequestContext requestContext,
        WorkInput? input = null,
        Action? onChanged = null)
        => new(
            WorkflowRunId.New(),
            workflow.Definition.Version,
            workflow.Definition.Name,
            WorkflowDefinitionFingerprint.Create(workflow),
            input,
            requestContext,
            DateTimeOffset.UtcNow,
            FlattenStepDefinitions(workflow.Steps).Select(WorkflowStepRunState.FromDefinition).ToList(),
            onChanged);

    public static WorkflowRunState Rehydrate(
        RegisteredWorkflow workflow,
        WorkflowRunPersistenceRecord record,
        Action? onChanged = null)
    {
        var persistedSteps = record.Steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
        var run = new WorkflowRunState(
            record.RunId,
            workflow.Definition.Version,
            record.DefinitionName,
            string.IsNullOrWhiteSpace(record.DefinitionFingerprint)
                ? WorkflowDefinitionFingerprint.Create(workflow)
                : record.DefinitionFingerprint,
            record.Input,
            record.RequestContext,
            record.CreatedAt,
            FlattenStepDefinitions(workflow.Steps)
                .Select(step => WorkflowStepRunState.FromDefinition(
                    step,
                    persistedSteps.TryGetValue(step.Name, out var persisted) ? persisted : null))
                .ToList(),
            onChanged);
        run.status = record.Status;
        run.pendingControlAction = ParsePendingControlAction(record.PendingControlAction);
        run.pendingControlRequestContext = record.PendingControlRequestContext;
        run.startedAt = record.StartedAt;
        run.completedAt = record.CompletedAt;
        run.messages = record.Messages;
        foreach (var receipt in record.ChildReceipts)
        {
            if (run.StepContainsWorker(receipt.StepName, receipt.WorkerId))
            {
                run.childReceipts[receipt.WorkerId] = receipt;
            }
        }
        return run;
    }

    public static WorkflowRunState FromPersistenceRecord(
        WorkflowRunPersistenceRecord record,
        Action? onChanged = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        var run = new WorkflowRunState(
            record.RunId,
            record.DefinitionVersion,
            record.DefinitionName,
            record.DefinitionFingerprint,
            record.Input,
            record.RequestContext,
            record.CreatedAt,
            record.Steps.Select(WorkflowStepRunState.FromPersistenceRecord).ToList(),
            onChanged);
        run.status = record.Status;
        run.pendingControlAction = ParsePendingControlAction(record.PendingControlAction);
        run.pendingControlRequestContext = record.PendingControlRequestContext;
        run.startedAt = record.StartedAt;
        run.completedAt = record.CompletedAt;
        run.messages = record.Messages;
        foreach (var receipt in record.ChildReceipts)
        {
            if (run.StepContainsWorker(receipt.StepName, receipt.WorkerId))
            {
                run.childReceipts[receipt.WorkerId] = receipt;
            }
        }
        return run;
    }

    public Task<WorkflowRunCompletion> WaitForCompletion()
        => this.completion.Task;

    public bool IsCompletionFaulted => this.completion.Task.IsFaulted;

    public bool TryClaimCompletion()
        => Interlocked.CompareExchange(ref this.completionClaimed, 1, 0) == 0;

    public bool TrySetCompletion(WorkflowRunCompletion value)
    {
        if (!this.TryClaimCompletion())
        {
            return false;
        }

        return this.completion.TrySetResult(value);
    }

    public bool TrySetClaimedCompletion(WorkflowRunCompletion value)
        => this.completion.TrySetResult(value);

    public bool TrySetClaimedCompletionException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return this.completion.TrySetException(exception);
    }

    public void MarkRunning()
    {
        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return;
            }

            this.startedAt ??= DateTimeOffset.UtcNow;
            this.status = WorkflowRunStatus.Running;
            this.completedAt = null;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.messages = [];
            this.onChanged?.Invoke();
        }
    }

    public WorkflowRunStatus GetStatus()
    {
        lock (this.sync)
        {
            return this.status;
        }
    }

    public WorkflowAction? GetPendingControlAction()
    {
        lock (this.sync)
        {
            return this.pendingControlAction;
        }
    }

    public WorkRequestContext? GetPendingControlRequestContext()
    {
        lock (this.sync)
        {
            return this.pendingControlRequestContext;
        }
    }

    public bool TryRecordAcceptedControlAction(
        WorkflowAction action,
        WorkRequestContext requestContext,
        out WorkflowRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        lock (this.sync)
        {
            if (this.status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled)
            {
                snapshot = this.ToSnapshotLocked();
                return false;
            }

            this.pendingControlAction = action;
            this.pendingControlRequestContext = requestContext.WithoutAuthorization();
            snapshot = this.ToSnapshotLocked();
            this.onChanged?.Invoke();
            return true;
        }
    }

    public WorkflowStepRunStatus GetStepStatus(string name)
    {
        lock (this.sync)
        {
            return this.steps.Single(step => step.Name == name).Status;
        }
    }

    public IReadOnlyList<WorkerId> GetOutstandingWorkerIds()
    {
        lock (this.sync)
        {
            var outstanding = new List<WorkerId>();
            foreach (var step in this.steps)
            {
                switch (step.Kind)
                {
                    case WorkflowStepKind.DispatchWork:
                    case WorkflowStepKind.DispatchEach:
                    case WorkflowStepKind.Parallel:
                    case WorkflowStepKind.Branch:
                        if (step.Status == WorkflowStepRunStatus.Completed)
                        {
                            outstanding.AddRange(step.WorkerIds);
                        }

                        break;
                    case WorkflowStepKind.Join:
                        if (step.Status == WorkflowStepRunStatus.Completed)
                        {
                            outstanding.Clear();
                        }

                        break;
                }
            }

            return [.. outstanding.Distinct()];
        }
    }

    public IReadOnlyList<WorkerId> GetAllWorkerIds()
    {
        lock (this.sync)
        {
            return [.. this.steps.SelectMany(step => step.WorkerIds).Distinct()];
        }
    }

    public void MarkStepRunning(string name, IReadOnlyList<WorkerId>? workerIds = null)
    {
        lock (this.sync)
        {
            var step = this.steps.Single(step => step.Name == name);
            if (workerIds is not null)
            {
                this.RemoveStepFromWorkerIndexLocked(step);
            }
            step.MarkRunning(workerIds);
            if (workerIds is not null)
            {
                this.AddStepToWorkerIndexLocked(step);
            }
            this.onChanged?.Invoke();
        }
    }

    public void MarkStepCompleted(string name, IReadOnlyList<WorkerId>? workerIds = null)
    {
        lock (this.sync)
        {
            var step = this.steps.Single(step => step.Name == name);
            if (workerIds is not null)
            {
                this.RemoveStepFromWorkerIndexLocked(step);
            }
            step.MarkCompleted(workerIds);
            if (workerIds is not null)
            {
                this.AddStepToWorkerIndexLocked(step);
            }
            this.onChanged?.Invoke();
        }
    }

    public IReadOnlyList<WorkerId> GetStepWorkerIds(string name)
    {
        lock (this.sync)
        {
            return [.. this.steps.Single(step => step.Name == name).WorkerIds];
        }
    }

    public bool TryGetStepWorkerIds(string name, out IReadOnlyList<WorkerId> workerIds)
    {
        lock (this.sync)
        {
            var step = this.steps.SingleOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));
            if (step is null)
            {
                workerIds = [];
                return false;
            }

            workerIds = [.. step.WorkerIds];
            return true;
        }
    }

    public WorkflowWorkerIdPage GetStepWorkerIdsPage(
        string selectedStepName,
        IReadOnlySet<string> readableDispatchStepNames,
        int skip,
        int take)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedStepName);
        ArgumentNullException.ThrowIfNull(readableDispatchStepNames);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        lock (this.sync)
        {
            var selected = this.steps.SingleOrDefault(
                step => string.Equals(step.Name, selectedStepName, StringComparison.Ordinal));
            if (selected is null)
            {
                return new WorkflowWorkerIdPage([], 0);
            }

            var page = new List<WorkerId>(take);
            var totalCount = 0;
            bool IsReadable(WorkerId workerId)
                => this.workerStepNames.TryGetValue(workerId, out var stepNames) &&
                    stepNames.Any(readableDispatchStepNames.Contains);

            void Include(
                IReadOnlyList<WorkerId> workerIds,
                bool omitResolved,
                bool requireReadable = false)
            {
                if (!omitResolved && !requireReadable)
                {
                    var firstIndex = Math.Max(0, skip - totalCount);
                    var remainingPageCapacity = take - page.Count;
                    var availableCount = Math.Max(0, workerIds.Count - firstIndex);
                    var copyCount = Math.Min(remainingPageCapacity, availableCount);
                    for (var index = 0; index < copyCount; index++)
                    {
                        page.Add(workerIds[firstIndex + index]);
                    }

                    totalCount += workerIds.Count;
                    return;
                }

                foreach (var workerId in workerIds)
                {
                    if ((omitResolved && this.childReceipts.ContainsKey(workerId)) ||
                        (requireReadable && !IsReadable(workerId)))
                    {
                        continue;
                    }

                    if (totalCount >= skip && page.Count < take)
                    {
                        page.Add(workerId);
                    }
                    totalCount++;
                }
            }

            if (selected.Kind is WorkflowStepKind.DispatchWork or WorkflowStepKind.DispatchEach)
            {
                if (readableDispatchStepNames.Contains(selected.Name))
                {
                    Include(selected.WorkerIds, omitResolved: false);
                }
                return new WorkflowWorkerIdPage(page, totalCount);
            }

            if (selected.Kind is WorkflowStepKind.Parallel or WorkflowStepKind.Branch)
            {
                if (selected.WorkerIds.Count > 0)
                {
                    Include(selected.WorkerIds, omitResolved: false, requireReadable: true);
                    return new WorkflowWorkerIdPage(page, totalCount);
                }

                foreach (var step in this.steps)
                {
                    if (readableDispatchStepNames.Contains(step.Name))
                    {
                        Include(step.WorkerIds, omitResolved: false);
                    }
                }
                return new WorkflowWorkerIdPage(page, totalCount);
            }

            if (selected.Kind == WorkflowStepKind.Join)
            {
                if (selected.WorkerIds.Count > 0)
                {
                    Include(selected.WorkerIds, omitResolved: false, requireReadable: true);
                    return new WorkflowWorkerIdPage(page, totalCount);
                }

                foreach (var step in this.steps)
                {
                    if (string.Equals(step.Name, selected.Name, StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (step.Kind == WorkflowStepKind.Join &&
                        step.Status == WorkflowStepRunStatus.Completed)
                    {
                        page.Clear();
                        totalCount = 0;
                    }
                    else if (step.Status == WorkflowStepRunStatus.Completed &&
                        readableDispatchStepNames.Contains(step.Name))
                    {
                        Include(step.WorkerIds, omitResolved: true);
                    }
                }
            }

            return new WorkflowWorkerIdPage(page, totalCount);
        }
    }

    public bool StepContainsWorker(string name, WorkerId workerId)
    {
        lock (this.sync)
        {
            foreach (var step in this.steps)
            {
                if (string.Equals(step.Name, name, StringComparison.Ordinal))
                {
                    return step.ContainsWorker(workerId);
                }
            }

            return false;
        }
    }

    public bool TryGetChildReceipt(
        WorkerId workerId,
        [NotNullWhen(true)] out WorkflowChildReceipt? receipt)
    {
        lock (this.sync)
        {
            return this.childReceipts.TryGetValue(workerId, out receipt);
        }
    }

    public IReadOnlyList<WorkflowChildReceipt> GetChildReceipts()
    {
        lock (this.sync)
        {
            return [.. this.childReceipts.Values];
        }
    }

    public bool RecordChildReceipt(WorkflowChildReceipt receipt)
    {
        lock (this.sync)
        {
            var step = this.steps.SingleOrDefault(
                step => string.Equals(step.Name, receipt.StepName, StringComparison.Ordinal));
            if (step?.ContainsWorker(receipt.WorkerId) != true)
            {
                return false;
            }

            if (this.childReceipts.TryGetValue(receipt.WorkerId, out var existing) &&
                (existing == receipt || existing.CompletedAt >= receipt.CompletedAt))
            {
                return false;
            }

            this.childReceipts[receipt.WorkerId] = receipt;
            this.onChanged?.Invoke();
            return true;
        }
    }

    public void FailStep(string name, IReadOnlyList<WorkMessage> stepMessages)
    {
        lock (this.sync)
        {
            this.steps.Single(step => step.Name == name).Fail(stepMessages);
            this.onChanged?.Invoke();
        }
    }

    public void RemoveStepWorkerId(string name, WorkerId workerId)
    {
        lock (this.sync)
        {
            var step = this.steps.Single(step => step.Name == name);
            step.RemoveWorkerId(workerId);
            this.RemoveWorkerStepIndexEntryLocked(workerId, step.Name);
            this.onChanged?.Invoke();
        }
    }

    private void RebuildWorkerStepIndexLocked()
    {
        this.workerStepNames.Clear();
        foreach (var step in this.steps)
        {
            this.AddStepToWorkerIndexLocked(step);
        }
    }

    private void AddStepToWorkerIndexLocked(WorkflowStepRunState step)
    {
        foreach (var workerId in step.WorkerIds)
        {
            if (!this.workerStepNames.TryGetValue(workerId, out var stepNames))
            {
                stepNames = new HashSet<string>(StringComparer.Ordinal);
                this.workerStepNames[workerId] = stepNames;
            }

            stepNames.Add(step.Name);
        }
    }

    private void RemoveStepFromWorkerIndexLocked(WorkflowStepRunState step)
    {
        foreach (var workerId in step.WorkerIds)
        {
            this.RemoveWorkerStepIndexEntryLocked(workerId, step.Name);
        }
    }

    private void RemoveWorkerStepIndexEntryLocked(WorkerId workerId, string stepName)
    {
        if (!this.workerStepNames.TryGetValue(workerId, out var stepNames))
        {
            return;
        }

        stepNames.Remove(stepName);
        if (stepNames.Count == 0)
        {
            this.workerStepNames.Remove(workerId);
        }
    }

    public WorkflowRunCompletion Complete()
    {
        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            this.status = WorkflowRunStatus.Completed;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.messages = [];
            this.completedAt = DateTimeOffset.UtcNow;
            this.onChanged?.Invoke();
            return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
        }
    }

    public WorkflowRunCompletion CreateFinalCompletion(
        WorkflowRunStatus finalStatus,
        IReadOnlyList<WorkMessage>? finalMessages = null,
        bool cancelOutstandingChildren = false)
    {
        if (!IsFinalStatus(finalStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalStatus),
                finalStatus,
                "A staged workflow completion must use a final status.");
        }

        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            var completionMessages = finalMessages ?? [];
            var finalCompletedAt = DateTimeOffset.UtcNow;
            return new WorkflowRunCompletion(
                finalStatus,
                this.ToSnapshotLocked(finalStatus, finalCompletedAt, completionMessages),
                completionMessages,
                cancelOutstandingChildren);
        }
    }

    public WorkflowRunCompletion CommitFinalCompletion(WorkflowRunCompletion finalCompletion)
    {
        ArgumentNullException.ThrowIfNull(finalCompletion);
        if (!finalCompletion.IsFinal)
        {
            throw new ArgumentException("Only a final workflow completion can be committed.", nameof(finalCompletion));
        }

        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            this.status = finalCompletion.Status;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.messages = finalCompletion.Messages;
            this.completedAt = finalCompletion.Run?.CompletedAt ?? DateTimeOffset.UtcNow;
            this.onChanged?.Invoke();
            return new WorkflowRunCompletion(
                this.status,
                this.ToSnapshotLocked(),
                this.messages,
                finalCompletion.CancelOutstandingChildren);
        }
    }

    public WorkflowRunCompletion Pause(IReadOnlyList<WorkMessage>? pauseMessages = null)
    {
        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            this.status = WorkflowRunStatus.Paused;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.completedAt = null;
            this.messages = pauseMessages ?? [];
            this.onChanged?.Invoke();
            return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
        }
    }

    public WorkflowRunCompletion Block(IReadOnlyList<WorkMessage> blockMessages)
    {
        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            this.status = WorkflowRunStatus.Blocked;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.completedAt = null;
            this.messages = blockMessages;
            this.onChanged?.Invoke();
            return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
        }
    }

    public WorkflowRunCompletion Cancel(bool cancelOutstandingChildren = false)
    {
        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            this.status = WorkflowRunStatus.Canceled;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.messages = [];
            this.completedAt = DateTimeOffset.UtcNow;
            this.onChanged?.Invoke();
            return new WorkflowRunCompletion(
                this.status,
                this.ToSnapshotLocked(),
                this.messages,
                cancelOutstandingChildren);
        }
    }

    public WorkflowRunCompletion Fail(IReadOnlyList<WorkMessage> failureMessages)
    {
        lock (this.sync)
        {
            if (IsFinalStatus(this.status))
            {
                return this.CurrentCompletionLocked();
            }

            this.status = WorkflowRunStatus.Failed;
            this.pendingControlAction = null;
            this.pendingControlRequestContext = null;
            this.messages = failureMessages;
            this.completedAt = DateTimeOffset.UtcNow;
            this.onChanged?.Invoke();
            return new WorkflowRunCompletion(this.status, this.ToSnapshotLocked(), this.messages);
        }
    }

    private WorkflowRunCompletion CurrentCompletionLocked()
        => new(this.status, this.ToSnapshotLocked(), this.messages);

    private static bool IsFinalStatus(WorkflowRunStatus status)
        => status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;

    public WorkflowRunSnapshot ToSnapshot()
    {
        lock (this.sync)
        {
            return this.ToSnapshotLocked();
        }
    }

    internal WorkflowRunSnapshot ToOperatorSnapshot(int maximumWorkerIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWorkerIds);

        lock (this.sync)
        {
            var retainedWorkerIds = new HashSet<WorkerId>();
            var stepSnapshots = this.steps
                .Select(step => step.ToOperatorSnapshot(retainedWorkerIds, maximumWorkerIds))
                .ToArray();
            var retainedReceipts = retainedWorkerIds
                .Select(workerId => this.childReceipts.TryGetValue(workerId, out var receipt) ? receipt : null)
                .OfType<WorkflowChildReceipt>()
                .ToArray();
            return new WorkflowRunSnapshot(
                this.Id,
                this.DefinitionName,
                this.status,
                this.Input,
                stepSnapshots,
                this.CreatedAt,
                this.startedAt,
                this.completedAt,
                this.messages,
                retainedReceipts)
            {
                DefinitionId = this.DefinitionVersion.DefinitionId,
            };
        }
    }

    public WorkflowRunPersistenceRecord ToPersistenceRecord(string? workSystemName)
    {
        lock (this.sync)
        {
            return this.ToPersistenceRecordLocked(
                workSystemName,
                this.status,
                this.completedAt,
                this.messages,
                this.pendingControlAction,
                this.pendingControlRequestContext);
        }
    }

    public WorkflowRunPersistenceRecord ToPersistenceRecord(
        string? workSystemName,
        WorkflowRunCompletion runCompletion)
    {
        ArgumentNullException.ThrowIfNull(runCompletion);
        if (!runCompletion.IsFinal)
        {
            throw new ArgumentException("Only a final workflow completion can be persisted as staged state.", nameof(runCompletion));
        }

        lock (this.sync)
        {
            return this.ToPersistenceRecordLocked(
                workSystemName,
                runCompletion.Status,
                runCompletion.Run?.CompletedAt ?? DateTimeOffset.UtcNow,
                runCompletion.Messages,
                persistedPendingControlAction: null,
                persistedPendingControlRequestContext: null);
        }
    }

    public WorkflowRunPersistenceRecord ToRunningPersistenceRecord(string? workSystemName)
    {
        lock (this.sync)
        {
            return this.ToPersistenceRecordLocked(
                workSystemName,
                WorkflowRunStatus.Running,
                persistedCompletedAt: null,
                persistedMessages: [],
                persistedPendingControlAction: null,
                persistedPendingControlRequestContext: null);
        }
    }

    public WorkflowRunPersistenceRecord ToPendingControlActionPersistenceRecord(
        string? workSystemName,
        WorkflowAction action,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        lock (this.sync)
        {
            return this.ToPersistenceRecordLocked(
                workSystemName,
                this.status,
                this.completedAt,
                this.messages,
                action,
                requestContext.WithoutAuthorization());
        }
    }

    private WorkflowRunPersistenceRecord ToPersistenceRecordLocked(
        string? workSystemName,
        WorkflowRunStatus persistedStatus,
        DateTimeOffset? persistedCompletedAt,
        IReadOnlyList<WorkMessage> persistedMessages,
        WorkflowAction? persistedPendingControlAction,
        WorkRequestContext? persistedPendingControlRequestContext)
        => new(
            workSystemName,
            this.Id,
            this.DefinitionVersion,
            this.DefinitionName,
            this.Input,
            this.RequestContext,
            persistedStatus,
            this.steps.Select(step => step.ToPersistenceRecord()).ToArray(),
            this.CreatedAt,
            this.startedAt,
            persistedCompletedAt,
            persistedMessages,
            this.childReceipts.Values.ToArray(),
            this.DefinitionFingerprint,
            persistedPendingControlAction?.ToString(),
            persistedPendingControlRequestContext);

    private static WorkflowAction? ParsePendingControlAction(string? value)
        => string.Equals(value, "Stop", StringComparison.Ordinal)
            ? WorkflowAction.Pause
            : Enum.TryParse<WorkflowAction>(value, ignoreCase: false, out var action)
                ? action
                : null;

    private static IEnumerable<WorkflowStepDefinition> FlattenStepDefinitions(IEnumerable<WorkflowStepDefinition> steps)
    {
        foreach (var step in steps)
        {
            yield return step;

            var childSteps = step switch
            {
                ParallelWorkflowStepDefinition parallel => parallel.Steps,
                BranchWorkflowStepDefinition branch => branch.Steps,
                _ => [],
            };

            foreach (var child in FlattenStepDefinitions(childSteps))
            {
                yield return child;
            }
        }
    }

    private WorkflowRunSnapshot ToSnapshotLocked()
        => new(
            this.Id,
            this.DefinitionName,
            this.status,
            this.Input,
            this.steps.Select(step => step.ToSnapshot()).ToArray(),
            this.CreatedAt,
            this.startedAt,
            this.completedAt,
            this.messages,
            this.childReceipts.Values.ToArray())
        {
            DefinitionId = this.DefinitionVersion.DefinitionId,
        };

    private WorkflowRunSnapshot ToSnapshotLocked(
        WorkflowRunStatus snapshotStatus,
        DateTimeOffset? snapshotCompletedAt,
        IReadOnlyList<WorkMessage> snapshotMessages)
        => new(
            this.Id,
            this.DefinitionName,
            snapshotStatus,
            this.Input,
            this.steps.Select(step => step.ToSnapshot()).ToArray(),
            this.CreatedAt,
            this.startedAt,
            snapshotCompletedAt,
            snapshotMessages,
            this.childReceipts.Values.ToArray())
        {
            DefinitionId = this.DefinitionVersion.DefinitionId,
        };

    private sealed class WorkflowStepRunState
    {
        private readonly List<WorkerId> workerIds = [];
        private readonly HashSet<WorkerId> workerIdLookup = [];
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

        public IReadOnlyList<WorkerId> WorkerIds => this.workerIds;

        public static WorkflowStepRunState FromDefinition(WorkflowStepDefinition definition)
            => new(definition.Name, definition.Kind);

        public static WorkflowStepRunState FromDefinition(
            WorkflowStepDefinition definition,
            WorkflowStepPersistenceRecord? record)
        {
            var state = new WorkflowStepRunState(definition.Name, definition.Kind);
            if (record is null)
            {
                return state;
            }

            state.Status = record.Status;
            state.StartedAt = record.StartedAt;
            state.CompletedAt = record.CompletedAt;
            state.messages = record.Messages;
            state.SetWorkerIds(record.WorkerIds);
            return state;
        }

        public static WorkflowStepRunState FromPersistenceRecord(WorkflowStepPersistenceRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            var state = new WorkflowStepRunState(record.Name, record.Kind)
            {
                Status = record.Status,
                StartedAt = record.StartedAt,
                CompletedAt = record.CompletedAt,
                messages = record.Messages,
            };
            state.SetWorkerIds(record.WorkerIds);
            return state;
        }

        public void MarkRunning(IReadOnlyList<WorkerId>? workerIds = null)
        {
            this.Status = WorkflowStepRunStatus.Running;
            this.StartedAt ??= DateTimeOffset.UtcNow;
            if (workerIds is not null)
            {
                this.SetWorkerIds(workerIds);
            }
        }

        public void MarkCompleted(IReadOnlyList<WorkerId>? workerIds = null)
        {
            this.Status = WorkflowStepRunStatus.Completed;
            this.StartedAt ??= DateTimeOffset.UtcNow;
            this.CompletedAt = DateTimeOffset.UtcNow;
            if (workerIds is not null)
            {
                this.SetWorkerIds(workerIds);
            }
        }

        public void Fail(IReadOnlyList<WorkMessage> failureMessages)
        {
            this.Status = WorkflowStepRunStatus.Failed;
            this.StartedAt ??= DateTimeOffset.UtcNow;
            this.CompletedAt = DateTimeOffset.UtcNow;
            this.messages = failureMessages;
        }

        public bool ContainsWorker(WorkerId workerId)
            => this.workerIdLookup.Contains(workerId);

        public void RemoveWorkerId(WorkerId workerId)
        {
            this.workerIds.Remove(workerId);
            this.workerIdLookup.Remove(workerId);
        }

        private void SetWorkerIds(IEnumerable<WorkerId> workerIds)
        {
            this.workerIds.Clear();
            this.workerIds.AddRange(workerIds);
            this.workerIdLookup.Clear();
            this.workerIdLookup.UnionWith(this.workerIds);
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

        public WorkflowStepRunSnapshot ToOperatorSnapshot(
            HashSet<WorkerId> retainedWorkerIds,
            int maximumWorkerIds)
        {
            var boundedWorkerIds = new List<WorkerId>(Math.Min(this.workerIds.Count, maximumWorkerIds));
            foreach (var workerId in this.workerIds)
            {
                if (retainedWorkerIds.Contains(workerId))
                {
                    boundedWorkerIds.Add(workerId);
                    continue;
                }

                if (retainedWorkerIds.Count >= maximumWorkerIds)
                {
                    break;
                }

                retainedWorkerIds.Add(workerId);
                boundedWorkerIds.Add(workerId);
            }

            return new WorkflowStepRunSnapshot(
                this.Name,
                this.Kind,
                this.Status,
                boundedWorkerIds,
                this.StartedAt,
                this.CompletedAt,
                this.messages);
        }

        public WorkflowStepPersistenceRecord ToPersistenceRecord()
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

internal sealed record WorkflowWorkerIdPage(
    IReadOnlyList<WorkerId> WorkerIds,
    int TotalCount);
