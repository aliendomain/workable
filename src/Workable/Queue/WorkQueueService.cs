namespace Workable;
internal sealed class WorkQueueService(
    WorkSystemCatalog catalog,
    WorkerOperations workers,
    WorkSystemQueueDiagnosticsTracker queueDiagnostics) :
    IWorkQueueService
{
    public Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(
            definitionId,
            input,
            options,
            WorkRequestContext.Create(WorkInvocationChannel.DotNet),
            cancellationToken);

    internal Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!catalog.TryGetWork(definitionId, out var registeredWork))
        {
            return Task.FromResult<IWorkerHandle>(Reject(WorkQueueOutcome.NotFound(definitionId.ToString())));
        }

        if (!registeredWork.Definition.Configuration.Invocation.Allows(requestContext.Origin.Channel))
        {
            return Task.FromResult<IWorkerHandle>(Reject(ChannelNotAllowed(registeredWork.Definition, requestContext.Origin.Channel)));
        }

        return workers.CreateWorker(registeredWork, input, options, requestContext, cancellationToken);
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
            WorkRequestContext.Create(WorkInvocationChannel.DotNet),
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

        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return Task.FromResult<IWorkerHandle>(Reject(WorkQueueOutcome.NotFound(name)));
        }

        if (!registeredWork.Definition.Configuration.Invocation.Allows(requestContext.Origin.Channel))
        {
            return Task.FromResult<IWorkerHandle>(Reject(ChannelNotAllowed(registeredWork.Definition, requestContext.Origin.Channel)));
        }

        return workers.CreateWorker(registeredWork, input, options, requestContext, cancellationToken);
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

    private WorkerHandle Reject(WorkQueueOutcome outcome)
    {
        queueDiagnostics.RecordRejected(outcome);
        return WorkerHandle.Rejected(outcome);
    }

    private static WorkQueueOutcome ChannelNotAllowed(
        WorkDefinition definition,
        WorkInvocationChannel channel)
        => WorkQueueOutcome.Invalid(
            definition.Id,
            [WorkMessage.Error(
                "workable.invocation.channel_not_allowed",
                $"Work '{definition.Name}' cannot be invoked through {DescribeChannel(channel)}.",
                "invocation.channel")]);

    private static string DescribeChannel(WorkInvocationChannel channel)
        => channel switch
        {
            WorkInvocationChannel.DotNet => ".NET",
            WorkInvocationChannel.HttpApi => "the HTTP API",
            WorkInvocationChannel.Mcp => "MCP",
            WorkInvocationChannel.SignalR => "SignalR",
            _ => channel.ToString(),
        };
}
