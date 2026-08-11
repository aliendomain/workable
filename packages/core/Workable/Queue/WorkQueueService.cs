namespace Workable;
internal sealed class WorkQueueService(
    WorkSystemCatalog catalog,
    WorkerOperations workers,
    WorkSystemQueueDiagnosticsTracker queueDiagnostics) :
    IWorkQueueService
{
    public void NotifyDurableWorkAvailable()
        => workers.NotifyDurableWorkAvailable();

    public Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(
            definitionId,
            input,
            options,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);

    internal Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!this.TryPrepareInput(input, out var preparedInput, out var invalidInputHandle))
        {
            return Task.FromResult<IWorkerHandle>(invalidInputHandle);
        }

        if (WorkflowProvenanceRules.ContainsRunIdentifier(preparedInput))
        {
            return Task.FromResult<IWorkerHandle>(Reject(ReservedWorkflowRunIdentifier()));
        }

        if (!catalog.TryGetWork(definitionId, out var registeredWork))
        {
            return Task.FromResult<IWorkerHandle>(Reject(WorkQueueOutcome.NotFound(definitionId.ToString())));
        }

        if (!registeredWork.Definition.Configuration.Invocation.Allows(requestContext.Channel))
        {
            return Task.FromResult<IWorkerHandle>(Reject(ChannelNotAllowed(registeredWork.Definition, requestContext.Channel)));
        }

        return workers.CreateWorker(registeredWork, preparedInput, options, requestContext, cancellationToken);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(definitionId, ToWorkInput(input), options, cancellationToken);

    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return this.Enqueue(
            name,
            input,
            options,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);
    }

    internal Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!this.TryPrepareInput(input, out var preparedInput, out var invalidInputHandle))
        {
            return Task.FromResult<IWorkerHandle>(invalidInputHandle);
        }

        if (WorkflowProvenanceRules.ContainsRunIdentifier(preparedInput))
        {
            return Task.FromResult<IWorkerHandle>(Reject(ReservedWorkflowRunIdentifier()));
        }

        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return Task.FromResult<IWorkerHandle>(Reject(WorkQueueOutcome.NotFound(name)));
        }

        if (!registeredWork.Definition.Configuration.Invocation.Allows(requestContext.Channel))
        {
            return Task.FromResult<IWorkerHandle>(Reject(ChannelNotAllowed(registeredWork.Definition, requestContext.Channel)));
        }

        return workers.CreateWorker(registeredWork, preparedInput, options, requestContext, cancellationToken);
    }

    internal Task<IWorkerHandle> EnqueueDelegated(
        string name,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!this.TryPrepareInput(input, out var preparedInput, out var invalidInputHandle))
        {
            return Task.FromResult<IWorkerHandle>(invalidInputHandle);
        }

        if (WorkflowProvenanceRules.ContainsRunIdentifier(preparedInput))
        {
            return Task.FromResult<IWorkerHandle>(this.Reject(ReservedWorkflowRunIdentifier()));
        }

        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return Task.FromResult<IWorkerHandle>(this.Reject(WorkQueueOutcome.NotFound(name)));
        }

        // The caller reached this path through a trusted, declared parent relationship. The child's direct queue
        // authorization and invocation-channel gate do not apply, while normal worker validation and coordination do.
        return workers.CreateWorker(registeredWork, preparedInput, options, requestContext, cancellationToken);
    }

    internal Task<IWorkerHandle> EnqueueWorkflowChild(
        string name,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        WorkflowProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requestContext);
        ArgumentNullException.ThrowIfNull(provenance);

        if (!this.TryPrepareInput(input, out var preparedInput, out var invalidInputHandle))
        {
            return Task.FromResult<IWorkerHandle>(invalidInputHandle);
        }

        if (!WorkflowProvenanceRules.HasExactRunIdentifier(preparedInput, provenance.RunId))
        {
            return Task.FromResult<IWorkerHandle>(this.Reject(InvalidWorkflowRunIdentifier(provenance.RunId)));
        }

        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return Task.FromResult<IWorkerHandle>(this.Reject(WorkQueueOutcome.NotFound(name)));
        }

        return workers.CreateWorker(
            registeredWork,
            preparedInput,
            options,
            requestContext,
            cancellationToken,
            provenance);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(name, ToWorkInput(input), options, cancellationToken);

    private static WorkInput? ToWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };

    internal WorkerHandle Reject(WorkQueueOutcome outcome)
    {
        queueDiagnostics.RecordRejected(outcome);
        return WorkerHandle.Rejected(outcome);
    }

    private bool TryPrepareInput(
        WorkInput? input,
        out WorkInput? preparedInput,
        out WorkerHandle invalidInputHandle)
    {
        preparedInput = WorkflowProvenanceRules.SnapshotInput(input);
        if (!WorkflowProvenanceRules.ContainsMalformedIdentifier(preparedInput))
        {
            invalidInputHandle = null!;
            return true;
        }

        invalidInputHandle = this.Reject(InvalidIdentifier());
        return false;
    }

    private static WorkQueueOutcome ChannelNotAllowed(
        WorkDefinition definition,
        WorkInvocationChannel channel)
        => WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.invocation.channel_not_allowed",
                $"Work '{definition.Name}' cannot be invoked through {DescribeChannel(channel)}.",
                "invocation.channel")]);

    private static WorkQueueOutcome ReservedWorkflowRunIdentifier()
        => WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.workflow.identifier.reserved",
                "The 'workflow-run' identifier is system-reserved and can be assigned only by Workable workflow dispatch.",
                "input.identifiers")]);

    private static WorkQueueOutcome InvalidIdentifier()
        => WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.identifier.invalid",
                "Work identifiers require non-empty type and value strings.",
                "input.identifiers")]);

    private static WorkQueueOutcome InvalidWorkflowRunIdentifier(WorkflowRunId runId)
        => WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.workflow.identifier.invalid",
                $"Workflow child input must contain the system-assigned workflow run identifier '{runId}'.",
                "input.identifiers")]);

    private static string DescribeChannel(WorkInvocationChannel channel)
        => channel switch
        {
            WorkInvocationChannel.InProcess => "in-process code",
            WorkInvocationChannel.HttpApi => "the HTTP API",
            WorkInvocationChannel.Mcp => "MCP",
            WorkInvocationChannel.SignalR => "SignalR",
            _ => channel.ToString(),
        };
}
