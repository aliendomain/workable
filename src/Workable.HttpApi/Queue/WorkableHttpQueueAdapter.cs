namespace Workable;

public sealed class WorkableHttpQueueAdapter
{
    public async Task<WorkableHttpWorkResult> Enqueue(
        IWorkSystemSession session,
        string name,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var handle = await session.Queue.Enqueue(name, CreateInput(request), request?.Options?.ToWorkerOptions(), cancellationToken);
        return await CreateQueueResult(handle, request, cancellationToken);
    }

    public async Task<WorkableHttpWorkResult> Enqueue(
        IWorkSystemSession session,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var handle = await session.Queue.Enqueue(definitionId, CreateInput(request), request?.Options?.ToWorkerOptions(), cancellationToken);
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
