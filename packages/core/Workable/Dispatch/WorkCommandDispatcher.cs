namespace Workable;

/// <summary>
/// Dispatches request/response-style work through a Workable system and returns queue plus completion status.
/// </summary>
/// <remarks>
/// This adapter is useful when an application wants to treat a work definition like a command handler while still
/// flowing through Workable queueing, authorization, execution, and completion semantics.
/// </remarks>
public sealed class WorkCommandDispatcher(
    IWorkSystemRegistry workSystems) : IWorkCommandDispatcher
{
    /// <summary>
    /// Dispatches work through the default Workable system.
    /// </summary>
    public Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string workName,
        TRequest request,
        WorkRequestContext requestContext,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Dispatch<TRequest, TResponse>(
            systemName: null,
            workName,
            request,
            requestContext,
            options,
            cancellationToken);

    /// <summary>
    /// Dispatches work through a specific named Workable system.
    /// </summary>
    public async Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
        string? systemName,
        string workName,
        TRequest request,
        WorkRequestContext requestContext,
        WorkDispatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workName);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!TryResolveSystem(workSystems, systemName, out var workSystem))
        {
            return CreateSystemNotFoundResult<TResponse>(systemName);
        }

        var session = await workSystem.CreateSession(requestContext, cancellationToken);
        var resolvedOptions = options ?? new WorkDispatchOptions();
        var handle = await session.Queue.Enqueue(
            workName,
            request,
            resolvedOptions.WorkerOptions,
            cancellationToken);

        if (!handle.QueueOutcome.IsAccepted)
        {
            return CreateResult<TResponse>(
                ToDispatchStatus(handle.QueueOutcome.Status),
                response: default,
                handle.WorkerId,
                handle.QueueOutcome.Messages,
                handle.QueueOutcome,
                completion: null);
        }

        if (resolvedOptions.Completion == WorkDispatchCompletion.ReturnAfterAccepted)
        {
            return CreateResult<TResponse>(
                WorkDispatchStatus.Accepted,
                response: default,
                handle.WorkerId,
                handle.QueueOutcome.Messages,
                handle.QueueOutcome,
                completion: null);
        }

        var completion = await handle.WaitForCompletion<TResponse>(cancellationToken);

        return CreateResult(
            ToDispatchStatus(completion.Status),
            completion.Output,
            handle.WorkerId,
            completion.Messages,
            handle.QueueOutcome,
            completion);
    }

    private static bool TryResolveSystem(
        IWorkSystemRegistry workSystems,
        string? systemName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWorkSystem? workSystem)
    {
        ArgumentNullException.ThrowIfNull(workSystems);

        if (string.IsNullOrWhiteSpace(systemName))
        {
            workSystem = workSystems.Default;
            return true;
        }

        return workSystems.TryGet(systemName, out workSystem);
    }

    private static WorkDispatchResult<TResponse> CreateSystemNotFoundResult<TResponse>(string? systemName)
    {
        var message = string.IsNullOrWhiteSpace(systemName)
            ? "The default Workable system is not registered."
            : $"The '{systemName}' Workable system is not registered.";
        var messages = new[]
        {
            WorkMessage.Error("workable.dispatch.system.not_found", message, "system"),
        };

        return CreateResult<TResponse>(
            WorkDispatchStatus.SystemNotFound,
            response: default,
            workerId: null,
            messages,
            queueOutcome: null,
            completion: null);
    }

    private static WorkDispatchResult<TResponse> CreateResult<TResponse>(
        WorkDispatchStatus status,
        TResponse? response,
        WorkerId? workerId,
        IReadOnlyList<WorkMessage> messages,
        WorkQueueOutcome? queueOutcome,
        WorkCompletion<TResponse>? completion)
    {
        var error = messages
            .FirstOrDefault(candidate => candidate.Severity.IsError() && !string.IsNullOrWhiteSpace(candidate.Text))
            ?? messages.FirstOrDefault(candidate => candidate.Severity.IsError());

        return new WorkDispatchResult<TResponse>(
            status,
            response,
            workerId,
            error?.Code,
            string.IsNullOrWhiteSpace(error?.Text)
                ? null
                : error.Text,
            messages,
            queueOutcome,
            completion);
    }

    private static WorkDispatchStatus ToDispatchStatus(WorkQueueStatus status)
        => status switch
        {
            WorkQueueStatus.Accepted => WorkDispatchStatus.Accepted,
            WorkQueueStatus.Invalid => WorkDispatchStatus.Invalid,
            WorkQueueStatus.Unauthorized => WorkDispatchStatus.Unauthorized,
            WorkQueueStatus.NotFound => WorkDispatchStatus.NotFound,
            _ => WorkDispatchStatus.Invalid,
        };

    private static WorkDispatchStatus ToDispatchStatus(WorkCompletionStatus status)
        => status switch
        {
            WorkCompletionStatus.Executing => WorkDispatchStatus.Executing,
            WorkCompletionStatus.Completed => WorkDispatchStatus.Completed,
            WorkCompletionStatus.Failed => WorkDispatchStatus.Failed,
            WorkCompletionStatus.Paused => WorkDispatchStatus.Paused,
            WorkCompletionStatus.Interrupted => WorkDispatchStatus.Interrupted,
            WorkCompletionStatus.Canceled => WorkDispatchStatus.Canceled,
            WorkCompletionStatus.Invalid => WorkDispatchStatus.Invalid,
            WorkCompletionStatus.NotFound => WorkDispatchStatus.NotFound,
            _ => WorkDispatchStatus.Invalid,
        };
}
