namespace Workable;

public sealed class WorkableHttpQueueAdapter(IDotNetWorkOriginProvider dotNetOriginProvider)
{
    public Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        string name,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => QueueCore(
            system,
            name,
            request,
            dotNetOriginProvider.CreateOrigin($"Queue HTTP-adapter work '{name}' through .NET."),
            cancellationToken);

    internal Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        string name,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
        => QueueCore(system, name, request, origin, cancellationToken);

    public Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => QueueCore(
            system,
            definitionId,
            request,
            dotNetOriginProvider.CreateOrigin($"Queue HTTP-adapter work definition '{definitionId.Value:D}' through .NET."),
            cancellationToken);

    internal Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
        => QueueCore(system, definitionId, request, origin, cancellationToken);

    internal static async Task<WorkableHttpWorkResult> QueueCore(
        IWorkSystem system,
        string name,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => await QueueCore(
            system,
            name,
            request,
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: $"Queue HTTP-adapter work '{name}' through .NET."),
            cancellationToken);

    internal static async Task<WorkableHttpWorkResult> QueueCore(
        IWorkSystem system,
        string name,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!system.Catalog.TryGet(name, out var definition))
        {
            var notFound = WorkQueueOutcome.NotFound(name);
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                notFound,
                WorkerId: null,
                Completion: null,
                Output: null,
                notFound.Messages);
        }

        if (!definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi))
        {
            var outcome = WorkQueueOutcome.Invalid(
                definition.Id,
                [WorkMessage.Error("workable.invocation.channel_not_allowed", $"Work '{name}' cannot be invoked through the HTTP API.", "invocation.channel")]);
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                outcome,
                WorkerId: null,
                Completion: null,
                Output: null,
                outcome.Messages);
        }

        var handle = await WorkableHttpOriginAwareSystem.Required(system).Enqueue(name, CreateInput(request), request?.Options?.ToWorkerOptions(), origin, cancellationToken);
        return await CreateQueueResult(handle, request, cancellationToken);
    }

    internal static async Task<WorkableHttpWorkResult> QueueCore(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => await QueueCore(
            system,
            definitionId,
            request,
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: $"Queue HTTP-adapter work definition '{definitionId.Value:D}' through .NET."),
            cancellationToken);

    internal static async Task<WorkableHttpWorkResult> QueueCore(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (!system.Catalog.TryGet(definitionId, out var definition))
        {
            var notFound = WorkQueueOutcome.NotFound(definitionId.ToString());
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                notFound,
                WorkerId: null,
                Completion: null,
                Output: null,
                notFound.Messages);
        }

        if (!definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi))
        {
            var outcome = WorkQueueOutcome.Invalid(
                definition.Id,
                [WorkMessage.Error("workable.invocation.channel_not_allowed", $"Work '{definition.Name}' cannot be invoked through the HTTP API.", "invocation.channel")]);
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                outcome,
                WorkerId: null,
                Completion: null,
                Output: null,
                outcome.Messages);
        }

        var handle = await WorkableHttpOriginAwareSystem.Required(system).Enqueue(definitionId, CreateInput(request), request?.Options?.ToWorkerOptions(), origin, cancellationToken);
        return await CreateQueueResult(handle, request, cancellationToken);
    }

    private static async Task<WorkableHttpWorkResult> CreateQueueResult(
        IWorkerHandle handle,
        WorkableHttpWorkRequest? request,
        CancellationToken cancellationToken)
    {
        if (!handle.QueueOutcome.IsAccepted)
        {
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                handle.QueueOutcome,
                handle.WorkerId,
                Completion: null,
                Output: null,
                handle.QueueOutcome.Messages);
        }

        var completionMode = request?.Completion ?? WorkableHttpCompletion.ReturnAfterAccepted;
        if (completionMode == WorkableHttpCompletion.ReturnAfterAccepted)
        {
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Accepted,
                handle.QueueOutcome,
                handle.WorkerId,
                Completion: null,
                Output: null,
                handle.QueueOutcome.Messages);
        }

        var completion = await handle.WaitForCompletion(cancellationToken);

        return new WorkableHttpWorkResult(
            ToHttpStatus(completion.Status),
            handle.QueueOutcome,
            handle.WorkerId,
            completion,
            completion.Output,
            completion.Messages);
    }

    private static WorkInput CreateInput(WorkableHttpWorkRequest? request)
    {
        var input = request?.Input is { } json
            ? WorkInput.FromJson(
                json.GetRawText(),
                subjectId: request.SubjectId,
                concurrencyKey: request.ConcurrencyKey,
                identifiers: request.Identifiers)
            : WorkInput.Empty;

        if (request is null)
        {
            return input;
        }

        if (request.SubjectId is { } subjectId && input.SubjectId is null)
        {
            input = input.WithSubject(subjectId);
        }

        if (request.ConcurrencyKey is { } concurrencyKey && input.ConcurrencyKey is null)
        {
            input = input.WithConcurrencyKey(concurrencyKey);
        }

        if (request.Identifiers is { Count: > 0 } identifiers)
        {
            input = input.WithIdentifiers(identifiers);
        }

        return input;
    }

    private static WorkableHttpWorkStatus ToHttpStatus(WorkCompletionStatus status)
        => status switch
        {
            WorkCompletionStatus.Completed => WorkableHttpWorkStatus.Completed,
            WorkCompletionStatus.Interrupted => WorkableHttpWorkStatus.Interrupted,
            WorkCompletionStatus.Canceled => WorkableHttpWorkStatus.Canceled,
            WorkCompletionStatus.Failed => WorkableHttpWorkStatus.Failed,
            _ => WorkableHttpWorkStatus.Failed,
        };
}
