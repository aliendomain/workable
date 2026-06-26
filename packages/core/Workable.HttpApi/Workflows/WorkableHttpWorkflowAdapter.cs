namespace Workable;

/// <summary>
/// Adapts workflow start and control requests to the HTTP API surface.
/// </summary>
internal sealed class WorkableHttpWorkflowAdapter
{
    /// <summary>
    /// Starts one workflow using the HTTP request contract.
    /// </summary>
    public async Task<WorkableHttpWorkflowStartResult> Start(
        IWorkSystem system,
        string workflowName,
        WorkRequestContext requestContext,
        WorkableHttpWorkflowStartRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentNullException.ThrowIfNull(requestContext);

        var runtime = ResolveRuntime(system).WorkflowRuntime;
        var handle = runtime.Start(workflowName, requestContext, cancellationToken);
        if (!handle.StartOutcome.IsAccepted)
        {
            return new WorkableHttpWorkflowStartResult(
                MapStartStatus(handle.StartOutcome.Status),
                null,
                null,
                handle.StartOutcome.Messages);
        }

        var runId = handle.RunId?.Value;
        if ((request?.Completion ?? WorkableHttpCompletion.ReturnAfterAccepted) == WorkableHttpCompletion.WaitForCompletion)
        {
            var completion = await handle.WaitForCompletion(cancellationToken);
            return new WorkableHttpWorkflowStartResult(
                WorkableHttpWorkflowStartStatus.Accepted,
                runId,
                WorkableHttpWorkflowRun.From(completion.Run ?? runtime.Get(handle.RunId!.Value)),
                completion.Messages);
        }

        return new WorkableHttpWorkflowStartResult(
            WorkableHttpWorkflowStartStatus.Accepted,
            runId,
            WorkableHttpWorkflowRun.From(handle.RunId is { } acceptedRunId ? runtime.Get(acceptedRunId) : null),
            handle.StartOutcome.Messages);
    }

    /// <summary>
    /// Executes one workflow action using the HTTP request contract.
    /// </summary>
    public Task<WorkableHttpWorkflowActionResult> Execute(
        IWorkSystem system,
        WorkflowRunId runId,
        WorkflowAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = ResolveRuntime(system).WorkflowRuntime.Execute(runId, action, requestContext);
        return Task.FromResult(new WorkableHttpWorkflowActionResult(
            MapActionStatus(outcome.Status),
            MapActionKind(action),
            outcome.RunId.Value,
            WorkableHttpWorkflowRun.From(outcome.Run),
            outcome.Messages));
    }

    private static InMemoryWorkSystem ResolveRuntime(IWorkSystem system)
        => system as InMemoryWorkSystem
            ?? throw new InvalidOperationException("Workflow HTTP routes require the built-in Workable system implementation.");

    private static WorkableHttpWorkflowStartStatus MapStartStatus(WorkflowStartStatus status)
        => status switch
        {
            WorkflowStartStatus.Accepted => WorkableHttpWorkflowStartStatus.Accepted,
            WorkflowStartStatus.NotFound => WorkableHttpWorkflowStartStatus.NotFound,
            WorkflowStartStatus.Unauthorized => WorkableHttpWorkflowStartStatus.Unauthorized,
            _ => WorkableHttpWorkflowStartStatus.Invalid,
        };

    private static WorkableHttpWorkflowActionStatus MapActionStatus(WorkflowActionStatus status)
        => status switch
        {
            WorkflowActionStatus.Accepted => WorkableHttpWorkflowActionStatus.Accepted,
            WorkflowActionStatus.NotFound => WorkableHttpWorkflowActionStatus.NotFound,
            WorkflowActionStatus.Unauthorized => WorkableHttpWorkflowActionStatus.Unauthorized,
            _ => WorkableHttpWorkflowActionStatus.Invalid,
        };

    private static WorkableHttpWorkflowActionKind MapActionKind(WorkflowAction action)
        => action switch
        {
            WorkflowAction.Stop => WorkableHttpWorkflowActionKind.Stop,
            _ => WorkableHttpWorkflowActionKind.Cancel,
        };
}
