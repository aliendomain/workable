namespace Workable;

/// <summary>
/// Adapts workflow start and control requests to the HTTP API surface.
/// </summary>
internal sealed class WorkableHttpWorkflowAdapter
{
    private static readonly WorkflowRunViewAdapter Views = new();

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
    public async Task<WorkableHttpWorkflowActionResult> Execute(
        IWorkSystem system,
        WorkflowRunId runId,
        WorkflowAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = await ResolveRuntime(system).WorkflowRuntime.Execute(runId, action, requestContext);
        return new WorkableHttpWorkflowActionResult(
            MapActionStatus(outcome.Status),
            MapActionKind(action),
            outcome.RunId.Value,
            WorkableHttpWorkflowRun.From(outcome.Run),
            outcome.Messages);
    }

    /// <summary>
    /// Reads one workflow-run operator detail payload using the HTTP query contract.
    /// </summary>
    public Task<WorkflowRunDetailView?> Run(
        IWorkSystem system,
        WorkflowRunId runId,
        WorkRequestContext requestContext,
        int childSampleSize = 3,
        CancellationToken cancellationToken = default)
        => Views.Run(system, requestContext, runId, childSampleSize, cancellationToken);

    /// <summary>
    /// Reads visible workflow runs using the HTTP query contract.
    /// </summary>
    public Task<WorkflowRunListView> Runs(
        IWorkSystem system,
        WorkRequestContext requestContext,
        bool includeFinal = false,
        string? definitionName = null,
        int childSampleSize = 3,
        CancellationToken cancellationToken = default)
        => Views.Runs(system, requestContext, includeFinal, definitionName, childSampleSize, cancellationToken);

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
            WorkflowAction.Start => WorkableHttpWorkflowActionKind.Start,
            WorkflowAction.Pause => WorkableHttpWorkflowActionKind.Pause,
            _ => WorkableHttpWorkflowActionKind.Cancel,
        };
}
