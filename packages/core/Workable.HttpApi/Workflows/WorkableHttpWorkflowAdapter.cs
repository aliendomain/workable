namespace Workable;

/// <summary>
/// Adapts workflow start and control requests to the HTTP API surface.
/// </summary>
internal sealed class WorkableHttpWorkflowAdapter(
    IWorkflowCommandDispatcher commands)
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

        var result = await commands.Start(
            system.Name,
            workflowName,
            requestContext,
            CreateInput(request),
            new WorkflowCommandOptions(
                request?.Completion == WorkableHttpCompletion.WaitForCompletion
                    ? WorkDispatchCompletion.WaitForCompletion
                    : WorkDispatchCompletion.ReturnAfterAccepted),
            cancellationToken);
        if (result.RunId is null)
        {
            return new WorkableHttpWorkflowStartResult(
                MapStartStatus(result.Status),
                null,
                null,
                result.Messages);
        }

        return new WorkableHttpWorkflowStartResult(
            WorkableHttpWorkflowStartStatus.Accepted,
            result.RunId.Value.Value,
            WorkableHttpWorkflowRun.From(result.Run),
            result.Messages);
    }

    private static WorkInput? CreateInput(WorkableHttpWorkflowStartRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        if (request.Input is { } input)
        {
            return WorkInput.FromJson(
                input.GetRawText(),
                subjectId: request.SubjectId,
                concurrencyKey: request.ConcurrencyKey,
                identifiers: request.Identifiers);
        }

        if (request.SubjectId is null &&
            request.ConcurrencyKey is null &&
            request.Identifiers is null)
        {
            return null;
        }

        return WorkInput.Empty with
        {
            SubjectId = request.SubjectId,
            ConcurrencyKey = request.ConcurrencyKey,
            Identifiers = request.Identifiers is null
                ? null
                : new HashSet<WorkIdentifier>(request.Identifiers),
        };
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

        var result = await commands.Execute(
            system.Name,
            runId,
            ToRunAction(action),
            requestContext,
            cancellationToken);
        return new WorkableHttpWorkflowActionResult(
            MapActionStatus(result.Status),
            MapActionKind(action),
            (result.RunId ?? runId).Value,
            WorkableHttpWorkflowRun.From(result.Run),
            result.Messages);
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
    /// Reads one paged child-worker slice for a selected workflow step.
    /// </summary>
    public Task<WorkflowStepChildWorkerQueryResult?> StepChildren(
        IWorkSystem system,
        WorkflowRunId runId,
        string stepName,
        WorkRequestContext requestContext,
        int skip = 0,
        int take = 25,
        CancellationToken cancellationToken = default)
        => Views.StepChildren(system, requestContext, runId, stepName, skip, take, cancellationToken);

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

    private static WorkableHttpWorkflowStartStatus MapStartStatus(WorkflowCommandStatus status)
        => status switch
        {
            WorkflowCommandStatus.Accepted or
            WorkflowCommandStatus.Running or
            WorkflowCommandStatus.Paused or
            WorkflowCommandStatus.Blocked or
            WorkflowCommandStatus.Completed or
            WorkflowCommandStatus.Failed or
            WorkflowCommandStatus.Canceled => WorkableHttpWorkflowStartStatus.Accepted,
            WorkflowCommandStatus.NotFound => WorkableHttpWorkflowStartStatus.NotFound,
            WorkflowCommandStatus.Unauthorized => WorkableHttpWorkflowStartStatus.Unauthorized,
            _ => WorkableHttpWorkflowStartStatus.Invalid,
        };

    private static WorkableHttpWorkflowActionStatus MapActionStatus(WorkflowCommandStatus status)
        => status switch
        {
            WorkflowCommandStatus.Accepted => WorkableHttpWorkflowActionStatus.Accepted,
            WorkflowCommandStatus.NotFound => WorkableHttpWorkflowActionStatus.NotFound,
            WorkflowCommandStatus.Unauthorized => WorkableHttpWorkflowActionStatus.Unauthorized,
            _ => WorkableHttpWorkflowActionStatus.Invalid,
        };

    private static WorkableHttpWorkflowActionKind MapActionKind(WorkflowAction action)
        => action switch
        {
            WorkflowAction.Start => WorkableHttpWorkflowActionKind.Start,
            WorkflowAction.Pause => WorkableHttpWorkflowActionKind.Pause,
            _ => WorkableHttpWorkflowActionKind.Cancel,
        };

    private static WorkflowRunAction ToRunAction(WorkflowAction action)
        => action switch
        {
            WorkflowAction.Start => WorkflowRunAction.Start,
            WorkflowAction.Pause => WorkflowRunAction.Pause,
            _ => WorkflowRunAction.Cancel,
        };
}
