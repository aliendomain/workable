namespace Workable;

/// <summary>
/// Builds operator-facing workflow run list and detail payloads.
/// </summary>
public sealed class WorkflowRunViewAdapter
{
    private const int DefaultChildSampleSize = 3;
    private static readonly WorkflowChildWorkerSummary EmptyChildSummary =
        new(0, 0, 0, 0, new Dictionary<WorkerState, int>());

    /// <summary>
    /// Lists visible workflow runs for operator summary screens.
    /// </summary>
    public async Task<WorkflowRunListView> Runs(
        IWorkSystem system,
        WorkRequestContext requestContext,
        bool includeFinal = false,
        string? definitionName = null,
        int childSampleSize = DefaultChildSampleSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        var inMemory = ResolveSystem(system);
        var runs = await inMemory.WorkflowRuntime.ListVisibleStates(
            requestContext,
            includeFinal,
            definitionName,
            cancellationToken);
        if (runs.Count == 0)
        {
            return new WorkflowRunListView(DateTimeOffset.UtcNow, []);
        }

        var workerLookup = await LoadWorkers(
            inMemory,
            runs.SelectMany(run => GetOutstandingWorkerIds(run.ToSnapshot())),
            cancellationToken);
        var items = runs
            .Select(run => CreateListItem(inMemory, run, workerLookup, childSampleSize))
            .ToArray();

        return new WorkflowRunListView(DateTimeOffset.UtcNow, items);
    }

    /// <summary>
    /// Builds one visible workflow-run detail payload.
    /// </summary>
    public async Task<WorkflowRunDetailView?> Run(
        IWorkSystem system,
        WorkRequestContext requestContext,
        WorkflowRunId runId,
        int childSampleSize = DefaultChildSampleSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        var inMemory = ResolveSystem(system);
        var run = await inMemory.WorkflowRuntime.GetVisibleState(runId, requestContext, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var snapshot = run.ToSnapshot();
        if (!inMemory.Workflows.TryGet(snapshot.DefinitionName, out var workflow))
        {
            return null;
        }

        var workerLookup = await LoadWorkers(
            inMemory,
            snapshot.Steps.SelectMany(step => step.WorkerIds),
            cancellationToken);
        return CreateDetail(run, workflow, workerLookup, childSampleSize);
    }

    /// <summary>
    /// Reads one paged child-worker slice for a selected workflow step.
    /// </summary>
    public async Task<WorkflowStepChildWorkerQueryResult?> StepChildren(
        IWorkSystem system,
        WorkRequestContext requestContext,
        WorkflowRunId runId,
        string stepName,
        int skip = 0,
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

        var inMemory = ResolveSystem(system);
        var run = await inMemory.WorkflowRuntime.GetVisibleState(runId, requestContext, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var snapshot = run.ToSnapshot();
        if (!inMemory.Workflows.TryGet(snapshot.DefinitionName, out var workflow))
        {
            return null;
        }

        if (!TryGetStepWorkerIds(workflow.Steps, snapshot, stepName, out var workerIds))
        {
            return null;
        }

        var normalizedSkip = Math.Max(0, skip);
        var normalizedTake = Math.Clamp(take, 1, 100);
        var pageIds = workerIds
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToArray();
        var pageWorkers = await LoadWorkers(inMemory, pageIds, cancellationToken);
        var receiptLookup = snapshot.ChildReceipts.ToDictionary(receipt => receipt.WorkerId);
        var pageStates = BuildChildStates(pageIds, pageWorkers, receiptLookup);
        var page = pageStates
            .Select(worker => new WorkflowChildWorkerView(
                worker.WorkerId.Value,
                worker.DefinitionName,
                worker.State,
                worker.CreatedAt,
                worker.UpdatedAt))
            .ToArray();

        return new WorkflowStepChildWorkerQueryResult(
            page,
            workerIds.Count,
            normalizedSkip,
            normalizedTake);
    }

    private static async Task<IReadOnlyDictionary<WorkerId, WorkerSnapshot?>> LoadWorkers(
        InMemoryWorkSystem system,
        IEnumerable<WorkerId> workerIds,
        CancellationToken cancellationToken)
    {
        var ids = workerIds
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<WorkerId, WorkerSnapshot?>();
        }

        var reads = ids.Select(async workerId => new KeyValuePair<WorkerId, WorkerSnapshot?>(
            workerId,
            await system.WorkerOperations.GetAuthoritative(workerId, cancellationToken)));
        var loaded = await Task.WhenAll(reads);
        return loaded.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static WorkflowRunListItemView CreateListItem(
        InMemoryWorkSystem system,
        WorkflowRunState run,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        int childSampleSize)
    {
        var snapshot = run.ToSnapshot();
        var receiptLookup = snapshot.ChildReceipts.ToDictionary(receipt => receipt.WorkerId);
        var outstanding = GetOutstandingWorkerIds(snapshot);
        var outstandingWorkers = BuildChildStates(outstanding, workers, receiptLookup);
        var outstandingSummary = CreateChildSummary(outstanding, outstandingWorkers);
        WorkflowStepOperatorView? current = null;
        if (system.Workflows.TryGet(snapshot.DefinitionName, out var workflow))
        {
            var detail = CreateDetail(run, workflow, workers, childSampleSize);
            current = detail.Steps.FirstOrDefault(IsCurrentStepStatus);
        }

        return new WorkflowRunListItemView(
            snapshot.Id.Value,
            snapshot.DefinitionName,
            snapshot.Status,
            run.RequestContext.Origin,
            snapshot.CreatedAt,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            current?.Name,
            current?.Kind,
            current?.Status,
            outstandingSummary,
            snapshot.Messages);
    }

    private static WorkflowRunDetailView CreateDetail(
        WorkflowRunState run,
        RegisteredWorkflow workflow,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        int childSampleSize)
    {
        var snapshot = run.ToSnapshot();
        var receiptLookup = snapshot.ChildReceipts.ToDictionary(receipt => receipt.WorkerId);
        var snapshotsByName = snapshot.Steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
        var workersByStepName = workers.Values
            .Where(worker => worker is not null)
            .Cast<WorkerSnapshot>()
            .GroupBy(GetWorkflowStepName, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key!, group => group.ToArray(), StringComparer.Ordinal);
        var steps = workflow.Steps
            .Select(step => CreateStepView(
                step,
                snapshot,
                snapshotsByName,
                workers,
                receiptLookup,
                workersByStepName,
                childSampleSize))
            .ToArray();
        var current = ResolveCurrentTopLevelStep(steps);
        var outstandingIds = GetOutstandingWorkerIds(snapshot);
        var outstandingWorkers = BuildChildStates(outstandingIds, workers, receiptLookup);
        return new WorkflowRunDetailView(
            snapshot.Id.Value,
            snapshot.DefinitionName,
            snapshot.Status,
            WorkflowAvailableActions.For(snapshot.Status),
            run.RequestContext.Origin,
            snapshot.CreatedAt,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            current?.Name,
            current?.Kind,
            current?.Status,
            CreateChildSummary(outstandingIds, outstandingWorkers),
            steps,
            snapshot.Messages);
    }

    private static WorkflowStepOperatorView CreateStepView(
        WorkflowStepDefinition step,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        switch (step)
        {
            case DispatchWorkflowStepDefinition dispatch:
                return CreateDispatchStepView(dispatch, run, snapshotsByName, receiptLookup, workersByStepName, childSampleSize);
            case DispatchEachWorkflowStepDefinition dispatchEach:
                return CreateDispatchEachStepView(dispatchEach, run, snapshotsByName, receiptLookup, workersByStepName, childSampleSize);
            case ParallelWorkflowStepDefinition parallel:
                return CreateParallelStepView(parallel, run, snapshotsByName, receiptLookup, workersByStepName, childSampleSize);
            case BranchWorkflowStepDefinition branch:
                return CreateBranchStepView(branch, run, snapshotsByName, receiptLookup, workersByStepName, childSampleSize);
            case JoinWorkflowStepDefinition join:
                return CreateJoinStepView(join, run, snapshotsByName, workers, receiptLookup, childSampleSize);
            default:
                throw new InvalidOperationException($"Unsupported workflow step kind '{step.Kind}'.");
        }
    }

    private static WorkflowStepOperatorView CreateDispatchStepView(
        DispatchWorkflowStepDefinition dispatch,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(dispatch.Name, out var snapshot);
        var childWorkers = workersByStepName.TryGetValue(dispatch.Name, out var matchedWorkers)
            ? matchedWorkers
            : [];
        var childIds = snapshot?.WorkerIds ?? childWorkers.Select(worker => worker.Id).ToArray();
        var childStates = BuildChildStates(childIds, childWorkers.ToDictionary(worker => worker.Id, static worker => (WorkerSnapshot?)worker), receiptLookup);
        return CreateOperatorStep(
            dispatch.Name,
            dispatch.Kind,
            ResolveDispatchStatus(snapshot, run.Status, childStates),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            childIds,
            childStates,
            [],
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateDispatchEachStepView(
        DispatchEachWorkflowStepDefinition dispatchEach,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(dispatchEach.Name, out var snapshot);
        var childWorkers = workersByStepName.TryGetValue(dispatchEach.Name, out var matchedWorkers)
            ? matchedWorkers
            : [];
        var childIds = snapshot?.WorkerIds ?? childWorkers.Select(worker => worker.Id).ToArray();
        var childStates = BuildChildStates(childIds, childWorkers.ToDictionary(worker => worker.Id, static worker => (WorkerSnapshot?)worker), receiptLookup);
        return CreateOperatorStep(
            dispatchEach.Name,
            dispatchEach.Kind,
            ResolveDispatchStatus(snapshot, run.Status, childStates, dispatchEach.CanceledChildBehavior),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            childIds,
            childStates,
            [],
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateParallelStepView(
        ParallelWorkflowStepDefinition parallel,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(parallel.Name, out var snapshot);
        var childSteps = parallel.Steps
            .Select(child => CreateStepView(
                child,
                run,
                snapshotsByName,
                new Dictionary<WorkerId, WorkerSnapshot?>(),
                receiptLookup,
                workersByStepName,
                childSampleSize))
            .ToArray();
        var allWorkers = parallel.Steps
            .SelectMany(GetWorkflowStepNames)
            .SelectMany(stepName => workersByStepName.TryGetValue(stepName, out var matchedWorkers) ? matchedWorkers : [])
            .ToArray();
        var childIds = snapshot?.WorkerIds ?? allWorkers.Select(worker => worker.Id).ToArray();
        var childStates = BuildChildStates(childIds, allWorkers.ToDictionary(worker => worker.Id, static worker => (WorkerSnapshot?)worker), receiptLookup);
        return CreateOperatorStep(
            parallel.Name,
            parallel.Kind,
            ResolveParallelStatus(snapshot, run.Status, childSteps),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            childIds,
            childStates,
            childSteps,
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateJoinStepView(
        JoinWorkflowStepDefinition join,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(join.Name, out var snapshot);
        var candidateWorkerIds = GetOutstandingWorkerIdsBeforeJoin(run, join.Name);
        var workerIds = candidateWorkerIds
            .Where(workerId =>
                !IsResolvedChild(workerId, workers, receiptLookup))
            .Distinct()
            .ToArray();
        var childWorkers = BuildChildStates(workerIds, workers, receiptLookup);
        return CreateOperatorStep(
            join.Name,
            join.Kind,
            ResolveJoinStatus(snapshot, run.Status, childWorkers),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            workerIds,
            childWorkers,
            [],
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateBranchStepView(
        BranchWorkflowStepDefinition branch,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(branch.Name, out var snapshot);
        var childSteps = branch.Steps
            .Select(child => CreateStepView(
                child,
                run,
                snapshotsByName,
                new Dictionary<WorkerId, WorkerSnapshot?>(),
                receiptLookup,
                workersByStepName,
                childSampleSize))
            .ToArray();
        var allWorkers = branch.Steps
            .SelectMany(GetWorkflowStepNames)
            .SelectMany(stepName => workersByStepName.TryGetValue(stepName, out var matchedWorkers) ? matchedWorkers : [])
            .ToArray();
        var childIds = snapshot?.WorkerIds ?? allWorkers.Select(worker => worker.Id).ToArray();
        var childStates = BuildChildStates(childIds, allWorkers.ToDictionary(worker => worker.Id, static worker => (WorkerSnapshot?)worker), receiptLookup);
        return CreateOperatorStep(
            branch.Name,
            branch.Kind,
            ResolveParallelStatus(snapshot, run.Status, childSteps),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            childIds,
            childStates,
            childSteps,
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateOperatorStep(
        string name,
        WorkflowStepKind kind,
        WorkflowOperatorNodeStatus status,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        IReadOnlyList<WorkerId> workerIds,
        IReadOnlyList<WorkflowChildState> workers,
        IReadOnlyList<WorkflowStepOperatorView> steps,
        IReadOnlyList<WorkMessage> messages,
        int childSampleSize)
    {
        var summary = CreateChildSummary(workerIds, workers);
        var sample = workers
            .OrderByDescending(worker => worker.UpdatedAt)
            .Take(Math.Max(0, childSampleSize))
            .Select(worker => new WorkflowChildWorkerView(
                worker.WorkerId.Value,
                worker.DefinitionName,
                worker.State,
                worker.CreatedAt,
                worker.UpdatedAt))
            .ToArray();
        return new WorkflowStepOperatorView(
            name,
            kind,
            status,
            startedAt,
            completedAt,
            summary,
            workerIds.Select(workerId => workerId.Value).ToArray(),
            sample,
            Math.Max(0, summary.Total - sample.Length),
            steps,
            messages);
    }

    private static WorkflowChildWorkerSummary CreateChildSummary(
        IReadOnlyList<WorkerId> workerIds,
        IReadOnlyList<WorkflowChildState> workers)
    {
        if (workerIds.Count == 0)
        {
            return EmptyChildSummary;
        }

        var counts = new Dictionary<WorkerState, int>();
        var active = 0;
        var final = 0;
        foreach (var worker in workers)
        {
            counts[worker.State] = counts.TryGetValue(worker.State, out var count)
                ? count + 1
                : 1;
            if (worker.State.IsFinal())
            {
                final++;
            }
            else
            {
                active++;
            }
        }

        return new WorkflowChildWorkerSummary(
            workerIds.Count,
            active,
            final,
            Math.Max(0, workerIds.Count - active - final),
            counts);
    }

    private static WorkflowOperatorNodeStatus ResolveDispatchStatus(
        WorkflowStepRunSnapshot? snapshot,
        WorkflowRunStatus runStatus,
        IReadOnlyList<WorkflowChildState> childWorkers,
        WorkflowCanceledChildBehavior? canceledChildBehavior = null)
    {
        if (snapshot is null)
        {
            if (childWorkers.Count == 0)
            {
                return WorkflowOperatorNodeStatus.Pending;
            }

            return ResolveDispatchChildStatus(runStatus, childWorkers, canceledChildBehavior);
        }

        if (snapshot.Status == WorkflowStepRunStatus.Pending)
        {
            return WorkflowOperatorNodeStatus.Pending;
        }

        if (snapshot.Status == WorkflowStepRunStatus.Failed)
        {
            return WorkflowOperatorNodeStatus.Failed;
        }

        if (snapshot.Status == WorkflowStepRunStatus.Running)
        {
            if (runStatus == WorkflowRunStatus.Paused)
            {
                return WorkflowOperatorNodeStatus.Paused;
            }

            if (runStatus == WorkflowRunStatus.Blocked)
            {
                return WorkflowOperatorNodeStatus.Blocked;
            }

            return runStatus == WorkflowRunStatus.Canceled
                ? WorkflowOperatorNodeStatus.Canceled
                : WorkflowOperatorNodeStatus.Running;
        }

        return ResolveDispatchChildStatus(runStatus, childWorkers, canceledChildBehavior);
    }

    private static WorkflowOperatorNodeStatus ResolveDispatchChildStatus(
        WorkflowRunStatus runStatus,
        IReadOnlyList<WorkflowChildState> childWorkers,
        WorkflowCanceledChildBehavior? canceledChildBehavior)
    {
        if (childWorkers.Any(worker => worker.State is WorkerState.Canceled or WorkerState.Canceling))
        {
            switch (canceledChildBehavior)
            {
                case WorkflowCanceledChildBehavior.Continue:
                    break;
                case WorkflowCanceledChildBehavior.Block:
                    return WorkflowOperatorNodeStatus.Blocked;
                default:
                    return WorkflowOperatorNodeStatus.Canceled;
            }
        }

        if (childWorkers.Any(worker => worker.State is WorkerState.Failed or WorkerState.Interrupted or WorkerState.Paused))
        {
            return runStatus == WorkflowRunStatus.Paused
                ? WorkflowOperatorNodeStatus.Paused
                : WorkflowOperatorNodeStatus.Blocked;
        }

        return childWorkers.Any(worker => !worker.State.IsFinal())
            ? WorkflowOperatorNodeStatus.WaitingOnChildren
            : WorkflowOperatorNodeStatus.Completed;
    }

    private static WorkflowOperatorNodeStatus ResolveParallelStatus(
        WorkflowStepRunSnapshot? snapshot,
        WorkflowRunStatus runStatus,
        IReadOnlyList<WorkflowStepOperatorView> childSteps)
    {
        if (snapshot is null || snapshot.Status == WorkflowStepRunStatus.Pending)
        {
            return WorkflowOperatorNodeStatus.Pending;
        }

        if (snapshot.Status == WorkflowStepRunStatus.Failed)
        {
            return WorkflowOperatorNodeStatus.Failed;
        }

        if (snapshot.Status == WorkflowStepRunStatus.Running)
        {
            if (runStatus == WorkflowRunStatus.Paused)
            {
                return WorkflowOperatorNodeStatus.Paused;
            }

            if (runStatus == WorkflowRunStatus.Blocked)
            {
                return WorkflowOperatorNodeStatus.Blocked;
            }

            return runStatus == WorkflowRunStatus.Canceled
                ? WorkflowOperatorNodeStatus.Canceled
                : WorkflowOperatorNodeStatus.Running;
        }

        if (childSteps.Any(step => step.Status == WorkflowOperatorNodeStatus.Failed))
        {
            return WorkflowOperatorNodeStatus.Failed;
        }

        if (childSteps.Any(step => step.Status == WorkflowOperatorNodeStatus.Canceled))
        {
            return WorkflowOperatorNodeStatus.Canceled;
        }

        if (childSteps.Any(step => step.Status == WorkflowOperatorNodeStatus.Paused))
        {
            return WorkflowOperatorNodeStatus.Paused;
        }

        if (childSteps.Any(step => step.Status == WorkflowOperatorNodeStatus.Blocked))
        {
            return WorkflowOperatorNodeStatus.Blocked;
        }

        return childSteps.Any(step => step.Status is WorkflowOperatorNodeStatus.Running or WorkflowOperatorNodeStatus.WaitingOnChildren)
            ? WorkflowOperatorNodeStatus.WaitingOnChildren
            : WorkflowOperatorNodeStatus.Completed;
    }

    private static WorkflowOperatorNodeStatus ResolveJoinStatus(
        WorkflowStepRunSnapshot? snapshot,
        WorkflowRunStatus runStatus,
        IReadOnlyList<WorkflowChildState> childWorkers)
    {
        if (snapshot is null || snapshot.Status == WorkflowStepRunStatus.Pending)
        {
            return WorkflowOperatorNodeStatus.Pending;
        }

        if (snapshot.Status == WorkflowStepRunStatus.Failed)
        {
            return WorkflowOperatorNodeStatus.Failed;
        }

        if (snapshot.Status == WorkflowStepRunStatus.Completed)
        {
            return WorkflowOperatorNodeStatus.Completed;
        }

        if (runStatus == WorkflowRunStatus.Paused)
        {
            return WorkflowOperatorNodeStatus.Paused;
        }

        if (runStatus == WorkflowRunStatus.Blocked)
        {
            return WorkflowOperatorNodeStatus.Blocked;
        }

        if (runStatus == WorkflowRunStatus.Canceled)
        {
            return WorkflowOperatorNodeStatus.Canceled;
        }

        return childWorkers.Count > 0
            ? WorkflowOperatorNodeStatus.WaitingOnChildren
            : WorkflowOperatorNodeStatus.Running;
    }

    private static IReadOnlyList<WorkerId> GetOutstandingWorkerIds(WorkflowRunSnapshot snapshot)
    {
        var outstanding = new List<WorkerId>();
        foreach (var step in snapshot.Steps)
        {
            switch (step.Kind)
            {
                case WorkflowStepKind.DispatchWork:
                case WorkflowStepKind.DispatchEach:
                case WorkflowStepKind.Parallel:
                case WorkflowStepKind.Branch:
                    if (step.Status == WorkflowStepRunStatus.Completed)
                    {
                        outstanding.AddRange(step.WorkerIds.Where(workerId => !IsResolvedChild(workerId, snapshot.ChildReceipts)));
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

    private static IReadOnlyList<WorkerId> GetOutstandingWorkerIdsBeforeJoin(WorkflowRunSnapshot run, string joinStepName)
    {
        var outstanding = new List<WorkerId>();
        foreach (var step in run.Steps)
        {
            if (string.Equals(step.Name, joinStepName, StringComparison.Ordinal))
            {
                if (step.WorkerIds.Count > 0)
                {
                    return step.WorkerIds;
                }

                return outstanding;
            }

            switch (step.Kind)
            {
                case WorkflowStepKind.DispatchWork:
                case WorkflowStepKind.DispatchEach:
                case WorkflowStepKind.Parallel:
                case WorkflowStepKind.Branch:
                    if (step.Status == WorkflowStepRunStatus.Completed)
                    {
                        outstanding.AddRange(step.WorkerIds.Where(workerId => !IsResolvedChild(workerId, run.ChildReceipts)));
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

    private static string? GetWorkflowStepName(WorkerSnapshot worker)
        => worker.WorkflowProvenance?.StepName;

    private static IEnumerable<string> GetWorkflowStepNames(WorkflowStepDefinition step)
    {
        yield return step.Name;

        var childSteps = step switch
        {
            ParallelWorkflowStepDefinition parallel => parallel.Steps,
            BranchWorkflowStepDefinition branch => branch.Steps,
            _ => [],
        };

        foreach (var childName in childSteps.SelectMany(GetWorkflowStepNames))
        {
            yield return childName;
        }
    }

    private static bool TryGetStepWorkerIds(
        IReadOnlyList<WorkflowStepDefinition> steps,
        WorkflowRunSnapshot snapshot,
        string stepName,
        out IReadOnlyList<WorkerId> workerIds)
    {
        foreach (var step in steps)
        {
            if (string.Equals(step.Name, stepName, StringComparison.Ordinal))
            {
                workerIds = GetStepWorkerIds(step, snapshot);
                return true;
            }

            if (step is ParallelWorkflowStepDefinition parallel &&
                TryGetStepWorkerIds(parallel.Steps, snapshot, stepName, out workerIds))
            {
                return true;
            }

            if (step is BranchWorkflowStepDefinition branch &&
                TryGetStepWorkerIds(branch.Steps, snapshot, stepName, out workerIds))
            {
                return true;
            }
        }

        workerIds = [];
        return false;
    }

    private static IReadOnlyList<WorkerId> GetStepWorkerIds(
        WorkflowStepDefinition step,
        WorkflowRunSnapshot snapshot)
    {
        var stepSnapshot = snapshot.Steps.FirstOrDefault(
            candidate => string.Equals(candidate.Name, step.Name, StringComparison.Ordinal));

        switch (step)
        {
            case DispatchWorkflowStepDefinition:
            case DispatchEachWorkflowStepDefinition:
                return stepSnapshot?.WorkerIds ?? [];
            case ParallelWorkflowStepDefinition parallel:
                if (stepSnapshot is { WorkerIds.Count: > 0 })
                {
                    return stepSnapshot.WorkerIds;
                }

                return [.. parallel.Steps
                    .SelectMany(child => GetStepWorkerIds(child, snapshot))
                    .Distinct()];
            case BranchWorkflowStepDefinition branch:
                if (stepSnapshot is { WorkerIds.Count: > 0 })
                {
                    return stepSnapshot.WorkerIds;
                }

                return [.. branch.Steps
                    .SelectMany(child => GetStepWorkerIds(child, snapshot))
                    .Distinct()];
            case JoinWorkflowStepDefinition join:
                if (stepSnapshot is { WorkerIds.Count: > 0 })
                {
                    return stepSnapshot.WorkerIds;
                }

                return GetOutstandingWorkerIdsBeforeJoin(snapshot, join.Name);
            default:
                return [];
        }
    }

    private static WorkflowStepOperatorView? ResolveCurrentTopLevelStep(
        IReadOnlyList<WorkflowStepOperatorView> steps)
        => steps.FirstOrDefault(IsCurrentStepStatus);

    private static bool IsCurrentStepStatus(WorkflowStepOperatorView step)
        => step.Status is WorkflowOperatorNodeStatus.Running
            or WorkflowOperatorNodeStatus.WaitingOnChildren
            or WorkflowOperatorNodeStatus.Paused
            or WorkflowOperatorNodeStatus.Blocked
            or WorkflowOperatorNodeStatus.Failed
            or WorkflowOperatorNodeStatus.Canceled;

    private static InMemoryWorkSystem ResolveSystem(IWorkSystem system)
        => system as InMemoryWorkSystem
            ?? throw new InvalidOperationException("Workflow run views require the built-in Workable system implementation.");

    private static IReadOnlyList<WorkflowChildState> BuildChildStates(
        IReadOnlyList<WorkerId> workerIds,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup)
        => [.. workerIds
            .Distinct()
            .Select(workerId => BuildChildState(workerId, workers, receiptLookup))
            .Where(state => state is not null)
            .Cast<WorkflowChildState>()];

    private static WorkflowChildState? BuildChildState(
        WorkerId workerId,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup)
    {
        if (workers.TryGetValue(workerId, out var worker) && worker is not null)
        {
            return new WorkflowChildState(
                worker.Id,
                worker.DefinitionName,
                worker.State,
                worker.CreatedAt,
                worker.UpdatedAt);
        }

        return receiptLookup.TryGetValue(workerId, out var receipt)
            ? new WorkflowChildState(
                receipt.WorkerId,
                receipt.DefinitionName,
                receipt.State,
                receipt.CompletedAt,
                receipt.CompletedAt)
            : null;
    }

    private static bool IsResolvedChild(
        WorkerId workerId,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receiptLookup)
        => workers.TryGetValue(workerId, out var worker) && worker?.State == WorkerState.Completed ||
            receiptLookup.TryGetValue(workerId, out var receipt) && receipt.CompletionStatus == WorkCompletionStatus.Completed;

    private static bool IsResolvedChild(WorkerId workerId, IReadOnlyList<WorkflowChildReceipt> receipts)
        => receipts.Any(receipt => receipt.WorkerId == workerId && receipt.CompletionStatus == WorkCompletionStatus.Completed);

    private sealed record WorkflowChildState(
        WorkerId WorkerId,
        string DefinitionName,
        WorkerState State,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
