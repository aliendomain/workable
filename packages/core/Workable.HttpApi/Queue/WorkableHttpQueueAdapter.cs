namespace Workable;

/// <summary>
/// Adapts HTTP queue requests to the core command-dispatch and queueing APIs.
/// </summary>
public sealed class WorkableHttpQueueAdapter(
    IWorkCommandDispatcher commands)
{
    /// <summary>
    /// Queues work through the selected system using the HTTP request contract.
    /// </summary>
    /// <param name="systemName">The target system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="name">The definition name to queue.</param>
    /// <param name="requestContext">The request context to record on the queued worker.</param>
    /// <param name="request">The optional HTTP queue request payload.</param>
    /// <param name="cancellationToken">A token that cancels the queue or completion wait.</param>
    /// <returns>The HTTP queue result.</returns>
    public async Task<WorkableHttpWorkResult> Enqueue(
        string? systemName,
        string name,
        WorkRequestContext requestContext,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requestContext);

        var result = await commands.Dispatch<WorkInput, object?>(
            systemName,
            name,
            CreateInput(request),
            requestContext,
            new WorkDispatchOptions(
                request?.Completion == WorkableHttpCompletion.WaitForCompletion
                    ? WorkDispatchCompletion.WaitForCompletion
                    : WorkDispatchCompletion.ReturnAfterAccepted,
                request?.Options?.ToWorkerOptions()),
            cancellationToken);

        return CreateQueueResult(result);
    }

    private static WorkableHttpWorkResult CreateQueueResult(
        WorkDispatchResult<object?> result)
    {
        var queueOutcome = result.QueueOutcome ?? WorkQueueOutcome.Invalid(result.Messages);
        if (!queueOutcome.IsAccepted)
        {
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                queueOutcome,
                result.WorkerId,
                Completion: null,
                Output: null,
                result.Messages);
        }

        if (result.Status == WorkDispatchStatus.Accepted)
        {
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Accepted,
                queueOutcome,
                result.WorkerId,
                Completion: null,
                Output: null,
                result.Messages);
        }

        var completion = result.Completion;

        return new WorkableHttpWorkResult(
            completion is null ? WorkableHttpWorkStatus.Failed : ToHttpStatus(completion.Status),
            queueOutcome,
            result.WorkerId,
            completion is null
                ? null
                : new WorkCompletion(
                    completion.Status,
                    completion.Worker,
                    completion.RawOutput,
                    completion.Messages),
            completion?.RawOutput,
            result.Messages);
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
