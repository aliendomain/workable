namespace Workable;

internal sealed class WorkflowRunState
{
    private readonly Lock sync = new();
    private readonly List<WorkflowStepRunState> steps;
    private readonly TaskCompletionSource<WorkflowRunCompletion> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<WorkMessage> messages = [];
    private WorkflowRunStatus status;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? completedAt;

    private WorkflowRunState(
        WorkflowRunId id,
        WorkflowDefinitionVersion definitionVersion,
        string definitionName,
        string definitionFingerprint,
        WorkRequestContext requestContext,
        DateTimeOffset createdAt,
        List<WorkflowStepRunState> steps)
    {
        this.Id = id;
        this.DefinitionVersion = definitionVersion;
        this.DefinitionName = definitionName;
        this.DefinitionFingerprint = definitionFingerprint;
        this.RequestContext = requestContext;
        this.CreatedAt = createdAt;
        this.steps = steps;
        this.status = WorkflowRunStatus.Running;
    }

    public WorkflowRunId Id { get; }

    public WorkflowDefinitionVersion DefinitionVersion { get; }

    public string DefinitionName { get; }

    public string DefinitionFingerprint { get; }

    public WorkRequestContext RequestContext { get; }

    public DateTimeOffset CreatedAt { get; }

    public static WorkflowRunState Create(RegisteredWorkflow workflow, WorkRequestContext requestContext)
        => new(
            WorkflowRunId.New(),
            workflow.Definition.Version,
            workflow.Definition.Name,
            WorkflowDefinitionFingerprint.Create(workflow),
            requestContext,
            DateTimeOffset.UtcNow,
            workflow.Steps.Select(WorkflowStepRunState.FromDefinition).ToList());

    public static WorkflowRunState Rehydrate(
        RegisteredWorkflow workflow,
        WorkflowRunPersistenceRecord record)
    {
        var persistedSteps = record.Steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
        var run = new WorkflowRunState(
            record.RunId,
            workflow.Definition.Version,
            record.DefinitionName,
            string.IsNullOrWhiteSpace(record.DefinitionFingerprint)
                ? WorkflowDefinitionFingerprint.Create(workflow)
                : record.DefinitionFingerprint,
            record.RequestContext,
            record.CreatedAt,
            workflow.Steps
                .Select(step => WorkflowStepRunState.FromDefinition(
                    step,
                    persistedSteps.TryGetValue(step.Name, out var persisted) ? persisted : null))
                .ToList());
        run.status = record.Status;
        run.startedAt = record.StartedAt;
        run.completedAt = record.CompletedAt;
        run.messages = record.Messages;
        return run;
    }

    public static WorkflowRunState FromPersistenceRecord(WorkflowRunPersistenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var run = new WorkflowRunState(
            record.RunId,
            record.DefinitionVersion,
            record.DefinitionName,
            record.DefinitionFingerprint,
            record.RequestContext,
            record.CreatedAt,
            record.Steps.Select(WorkflowStepRunState.FromPersistenceRecord).ToList());
        run.status = record.Status;
        run.startedAt = record.StartedAt;
        run.completedAt = record.CompletedAt;
        run.messages = record.Messages;
        return run;
    }

    public Task<WorkflowRunCompletion> WaitForCompletion()
        => this.completion.Task;

    public void TrySetCompletion(WorkflowRunCompletion value)
        => this.completion.TrySetResult(value);

    public void MarkRunning()
    {
        lock (this.sync)
        {
            this.startedAt ??= DateTimeOffset.UtcNow;
            this.status = WorkflowRunStatus.Running;
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
    }

    public void MarkStepRunning(string name, IReadOnlyList<WorkerId>? workerIds = null)
    {
        lock (this.sync)
        {
            this.steps.Single(step => step.Name == name).MarkRunning(workerIds);
        }
    }

    public void MarkStepCompleted(string name, IReadOnlyList<WorkerId>? workerIds = null)
    {
        lock (this.sync)
        {
            this.steps.Single(step => step.Name == name).MarkCompleted(workerIds);
        }
    }

    public IReadOnlyList<WorkerId> GetStepWorkerIds(string name)
    {
        lock (this.sync)
        {
            return [.. this.steps.Single(step => step.Name == name).WorkerIds];
        }
    }

    public void FailStep(string name, IReadOnlyList<WorkMessage> stepMessages)
    {
        lock (this.sync)
        {
            this.steps.Single(step => step.Name == name).Fail(stepMessages);
        }
    }

    public void RemoveStepWorkerId(string name, WorkerId workerId)
    {
        lock (this.sync)
        {
            this.steps.Single(step => step.Name == name).RemoveWorkerId(workerId);
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

    public WorkflowRunPersistenceRecord ToPersistenceRecord(
        WorkSystemId workSystemId,
        string? workSystemName)
    {
        lock (this.sync)
        {
            return new WorkflowRunPersistenceRecord(
                workSystemId,
                workSystemName,
                this.Id,
                this.DefinitionVersion,
                this.DefinitionName,
                this.RequestContext,
                this.status,
                this.steps.Select(step => step.ToPersistenceRecord()).ToArray(),
                this.CreatedAt,
                this.startedAt,
                this.completedAt,
                this.messages,
                this.DefinitionFingerprint);
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

    private sealed class WorkflowStepRunState
    {
        private readonly List<WorkerId> workerIds = [];
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
            state.workerIds.AddRange(record.WorkerIds);
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
            state.workerIds.AddRange(record.WorkerIds);
            return state;
        }

        public void MarkRunning(IReadOnlyList<WorkerId>? workerIds = null)
        {
            this.Status = WorkflowStepRunStatus.Running;
            this.StartedAt ??= DateTimeOffset.UtcNow;
            if (workerIds is not null)
            {
                this.workerIds.Clear();
                this.workerIds.AddRange(workerIds);
            }
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

        public void RemoveWorkerId(WorkerId workerId)
            => this.workerIds.Remove(workerId);

        public WorkflowStepRunSnapshot ToSnapshot()
            => new(
                this.Name,
                this.Kind,
                this.Status,
                this.workerIds.ToArray(),
                this.StartedAt,
                this.CompletedAt,
                this.messages);

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
