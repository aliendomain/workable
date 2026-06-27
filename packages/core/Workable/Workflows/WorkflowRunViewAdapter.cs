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
        var runs = inMemory.WorkflowRuntime.ListVisibleStates(requestContext, includeFinal, definitionName);
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
        var run = inMemory.WorkflowRuntime.GetVisibleState(runId, requestContext);
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
        var outstanding = GetOutstandingWorkerIds(snapshot);
        var outstandingWorkers = outstanding
            .Select(workerId => workers.TryGetValue(workerId, out var worker) ? worker : null)
            .ToArray();
        var outstandingSummary = CreateChildSummary(outstanding, outstandingWorkers);
        WorkflowStepOperatorView? current = null;
        if (system.Workflows.TryGet(snapshot.DefinitionName, out var workflow))
        {
            var detail = CreateDetail(run, workflow, workers, childSampleSize);
            current = detail.Steps.FirstOrDefault(step => step.Status is WorkflowOperatorNodeStatus.Running or WorkflowOperatorNodeStatus.WaitingOnChildren or WorkflowOperatorNodeStatus.Failed or WorkflowOperatorNodeStatus.Canceled);
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
                workersByStepName,
                childSampleSize))
            .ToArray();
        var current = ResolveCurrentTopLevelStep(steps);
        var outstandingIds = GetOutstandingWorkerIds(snapshot);
        var outstandingWorkers = outstandingIds
            .Select(workerId => workers.TryGetValue(workerId, out var worker) ? worker : null)
            .ToArray();
        return new WorkflowRunDetailView(
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
            CreateChildSummary(outstandingIds, outstandingWorkers),
            steps,
            snapshot.Messages);
    }

    private static WorkflowStepOperatorView CreateStepView(
        WorkflowStepDefinition step,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        switch (step)
        {
            case DispatchWorkflowStepDefinition dispatch:
                return CreateDispatchStepView(dispatch, run, snapshotsByName, workersByStepName, childSampleSize);
            case ParallelWorkflowStepDefinition parallel:
                return CreateParallelStepView(parallel, run, snapshotsByName, workersByStepName, childSampleSize);
            case JoinWorkflowStepDefinition join:
                return CreateJoinStepView(join, run, snapshotsByName, workers, childSampleSize);
            default:
                throw new InvalidOperationException($"Unsupported workflow step kind '{step.Kind}'.");
        }
    }

    private static WorkflowStepOperatorView CreateDispatchStepView(
        DispatchWorkflowStepDefinition dispatch,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(dispatch.Name, out var snapshot);
        var childWorkers = workersByStepName.TryGetValue(dispatch.Name, out var matchedWorkers)
            ? matchedWorkers
            : [];
        var childIds = snapshot?.WorkerIds ?? childWorkers.Select(worker => worker.Id).ToArray();
        return CreateOperatorStep(
            dispatch.Name,
            dispatch.Kind,
            ResolveDispatchStatus(snapshot, run.Status, childWorkers),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            childIds,
            childWorkers,
            [],
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateParallelStepView(
        ParallelWorkflowStepDefinition parallel,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStepName,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(parallel.Name, out var snapshot);
        var childSteps = parallel.Steps
            .OfType<DispatchWorkflowStepDefinition>()
            .Select(child => CreateDispatchStepView(child, run, snapshotsByName, workersByStepName, childSampleSize))
            .ToArray();
        var allWorkers = parallel.Steps
            .SelectMany(step => workersByStepName.TryGetValue(step.Name, out var matchedWorkers) ? matchedWorkers : [])
            .ToArray();
        return CreateOperatorStep(
            parallel.Name,
            parallel.Kind,
            ResolveParallelStatus(snapshot, run.Status, childSteps),
            snapshot?.StartedAt,
            snapshot?.CompletedAt,
            snapshot?.WorkerIds ?? allWorkers.Select(worker => worker.Id).ToArray(),
            allWorkers,
            childSteps,
            snapshot?.Messages ?? [],
            childSampleSize);
    }

    private static WorkflowStepOperatorView CreateJoinStepView(
        JoinWorkflowStepDefinition join,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshotsByName,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        int childSampleSize)
    {
        snapshotsByName.TryGetValue(join.Name, out var snapshot);
        var workerIds = GetOutstandingWorkerIdsBeforeJoin(run, join.Name);
        var childWorkers = workerIds
            .Select(workerId => workers.TryGetValue(workerId, out var worker) ? worker : null)
            .ToArray();
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

    private static WorkflowStepOperatorView CreateOperatorStep(
        string name,
        WorkflowStepKind kind,
        WorkflowOperatorNodeStatus status,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        IReadOnlyList<WorkerId> workerIds,
        IReadOnlyList<WorkerSnapshot?> workers,
        IReadOnlyList<WorkflowStepOperatorView> steps,
        IReadOnlyList<WorkMessage> messages,
        int childSampleSize)
    {
        var summary = CreateChildSummary(workerIds, workers);
        var sample = workers
            .Where(worker => worker is not null)
            .Cast<WorkerSnapshot>()
            .OrderByDescending(worker => worker.UpdatedAt)
            .Take(Math.Max(0, childSampleSize))
            .Select(worker => new WorkflowChildWorkerView(
                worker.Id.Value,
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
        IReadOnlyList<WorkerSnapshot?> workers)
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
            if (worker is null)
            {
                continue;
            }

            counts[worker.State] = counts.TryGetValue(worker.State, out var count)
                ? count + 1
                : 1;
            if (worker.IsFinal)
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
        IReadOnlyList<WorkerSnapshot> childWorkers)
    {
        if (snapshot is null)
        {
            if (childWorkers.Count == 0)
            {
                return WorkflowOperatorNodeStatus.Pending;
            }

            if (childWorkers.Any(worker => worker.State is WorkerState.Failed or WorkerState.Interrupted))
            {
                return WorkflowOperatorNodeStatus.Failed;
            }

            if (childWorkers.Any(worker => worker.State is WorkerState.Canceled or WorkerState.Canceling))
            {
                return WorkflowOperatorNodeStatus.Canceled;
            }

            return childWorkers.Any(worker => !worker.IsFinal)
                ? WorkflowOperatorNodeStatus.WaitingOnChildren
                : WorkflowOperatorNodeStatus.Completed;
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
            return runStatus == WorkflowRunStatus.Canceled
                ? WorkflowOperatorNodeStatus.Canceled
                : WorkflowOperatorNodeStatus.Running;
        }

        if (childWorkers.Any(worker => worker.State is WorkerState.Failed or WorkerState.Interrupted))
        {
            return WorkflowOperatorNodeStatus.Failed;
        }

        if (childWorkers.Any(worker => worker.State is WorkerState.Canceled or WorkerState.Canceling))
        {
            return WorkflowOperatorNodeStatus.Canceled;
        }

        return childWorkers.Any(worker => !worker.IsFinal)
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

        return childSteps.Any(step => step.Status is WorkflowOperatorNodeStatus.Running or WorkflowOperatorNodeStatus.WaitingOnChildren)
            ? WorkflowOperatorNodeStatus.WaitingOnChildren
            : WorkflowOperatorNodeStatus.Completed;
    }

    private static WorkflowOperatorNodeStatus ResolveJoinStatus(
        WorkflowStepRunSnapshot? snapshot,
        WorkflowRunStatus runStatus,
        IReadOnlyList<WorkerSnapshot?> childWorkers)
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

        if (runStatus == WorkflowRunStatus.Canceled)
        {
            return WorkflowOperatorNodeStatus.Canceled;
        }

        return childWorkers.Any(worker => worker is not null)
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
                case WorkflowStepKind.Parallel:
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

        return outstanding;
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
                case WorkflowStepKind.Parallel:
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

        return outstanding;
    }

    private static string? GetWorkflowStepName(WorkerSnapshot worker)
        => worker.Identifiers
            .FirstOrDefault(identifier => string.Equals(identifier.Type, "workflow-step", StringComparison.Ordinal))
            .Value;

    private static WorkflowStepOperatorView? ResolveCurrentTopLevelStep(
        IReadOnlyList<WorkflowStepOperatorView> steps)
        => steps.FirstOrDefault(step => step.Status is WorkflowOperatorNodeStatus.Running or WorkflowOperatorNodeStatus.WaitingOnChildren or WorkflowOperatorNodeStatus.Failed or WorkflowOperatorNodeStatus.Canceled);

    private static InMemoryWorkSystem ResolveSystem(IWorkSystem system)
        => system as InMemoryWorkSystem
            ?? throw new InvalidOperationException("Workflow run views require the built-in Workable system implementation.");
}
