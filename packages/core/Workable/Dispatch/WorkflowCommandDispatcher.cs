namespace Workable;

/// <summary>
/// Dispatches workflow commands through a Workable system and returns start, action, and completion status.
/// </summary>
/// <remarks>
/// This adapter gives in-process code the same system-resolution and caller-context pattern that
/// <see cref="IWorkCommandDispatcher"/> provides for queued work.
/// </remarks>
public sealed class WorkflowCommandDispatcher(
    IWorkSystemRegistry workSystems) : IWorkflowCommandDispatcher
{
    /// <summary>
    /// Starts a workflow through the default Workable system.
    /// </summary>
    public Task<WorkflowCommandResult> Start(
        string workflowName,
        WorkRequestContext requestContext,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Start(
            workflowName,
            requestContext,
            input: null,
            options,
            cancellationToken);

    /// <summary>
    /// Starts a workflow through the default Workable system with workflow input.
    /// </summary>
    public Task<WorkflowCommandResult> Start(
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Start(
            systemName: null,
            workflowName,
            requestContext,
            input,
            options,
            cancellationToken);

    /// <summary>
    /// Starts a workflow through a specific named Workable system.
    /// </summary>
    public async Task<WorkflowCommandResult> Start(
        string? systemName,
        string workflowName,
        WorkRequestContext requestContext,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
        => await this.Start(
            systemName,
            workflowName,
            requestContext,
            input: null,
            options,
            cancellationToken);

    /// <summary>
    /// Starts a workflow through a specific named Workable system with workflow input.
    /// </summary>
    public async Task<WorkflowCommandResult> Start(
        string? systemName,
        string workflowName,
        WorkRequestContext requestContext,
        WorkInput? input,
        WorkflowCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowName);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!TryResolveRuntime(workSystems, systemName, out var runtime, out var systemNotFound))
        {
            return systemNotFound;
        }

        var handle = runtime.Start(workflowName, requestContext, input, cancellationToken);
        if (!handle.StartOutcome.IsAccepted)
        {
            return CreateResult(
                ToCommandStatus(handle.StartOutcome.Status),
                handle.RunId,
                runStatus: null,
                run: null,
                handle.StartOutcome.Messages);
        }

        var runId = handle.RunId;
        if ((options?.Completion ?? WorkDispatchCompletion.WaitForCompletion) == WorkDispatchCompletion.ReturnAfterAccepted)
        {
            return CreateResult(
                WorkflowCommandStatus.Accepted,
                runId,
                run: runId is { } acceptedRunId ? runtime.Get(acceptedRunId) : null,
                handle.StartOutcome.Messages);
        }

        var completion = await handle.WaitForCompletion(cancellationToken);
        var completedRun = completion.Run ?? (runId is { } completedRunId ? runtime.Get(completedRunId) : null);
        return CreateResult(
            ToCommandStatus(completion.Status),
            runId,
            completedRun,
            completion.Messages);
    }

    /// <summary>
    /// Executes a workflow action through the default Workable system.
    /// </summary>
    public Task<WorkflowCommandResult> Execute(
        WorkflowRunId runId,
        WorkflowRunAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
        => this.Execute(
            systemName: null,
            runId,
            action,
            requestContext,
            cancellationToken);

    /// <summary>
    /// Executes a workflow action through a specific named Workable system.
    /// </summary>
    public async Task<WorkflowCommandResult> Execute(
        string? systemName,
        WorkflowRunId runId,
        WorkflowRunAction action,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveRuntime(workSystems, systemName, out var runtime, out var systemNotFound))
        {
            return systemNotFound;
        }

        var outcome = await runtime.Execute(runId, ToWorkflowAction(action), requestContext);
        return CreateResult(
            ToCommandStatus(outcome.Status),
            outcome.RunId,
            outcome.Run,
            outcome.Messages);
    }

    private static bool TryResolveRuntime(
        IWorkSystemRegistry workSystems,
        string? systemName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkflowRuntime? runtime,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out WorkflowCommandResult? systemNotFound)
    {
        ArgumentNullException.ThrowIfNull(workSystems);

        if (string.IsNullOrWhiteSpace(systemName))
        {
            runtime = ((InMemoryWorkSystem)workSystems.Default).WorkflowRuntime;
            systemNotFound = null;
            return true;
        }

        if (workSystems.TryGet(systemName, out var workSystem))
        {
            runtime = ((InMemoryWorkSystem)workSystem).WorkflowRuntime;
            systemNotFound = null;
            return true;
        }

        runtime = null;
        systemNotFound = CreateSystemNotFoundResult(systemName);
        return false;
    }

    private static WorkflowCommandResult CreateSystemNotFoundResult(string? systemName)
    {
        var message = string.IsNullOrWhiteSpace(systemName)
            ? "The default Workable system is not registered."
            : $"The '{systemName}' Workable system is not registered.";
        var messages = new[]
        {
            WorkMessage.Error("workable.workflow.dispatch.system.not_found", message, "system"),
        };

        return CreateResult(
            WorkflowCommandStatus.SystemNotFound,
            runId: null,
            runStatus: null,
            run: null,
            messages);
    }

    private static WorkflowCommandResult CreateResult(
        WorkflowCommandStatus status,
        WorkflowRunId? runId,
        WorkflowRunSnapshot? run,
        IReadOnlyList<WorkMessage> messages)
        => CreateResult(
            status,
            runId,
            run?.Status,
            run,
            messages);

    private static WorkflowCommandResult CreateResult(
        WorkflowCommandStatus status,
        WorkflowRunId? runId,
        WorkflowRunStatus? runStatus,
        WorkflowRunSnapshot? run,
        IReadOnlyList<WorkMessage> messages)
    {
        var error = messages
            .FirstOrDefault(candidate => candidate.Severity.IsError() && !string.IsNullOrWhiteSpace(candidate.Text))
            ?? messages.FirstOrDefault(candidate => candidate.Severity.IsError());

        return new WorkflowCommandResult(
            status,
            runId,
            runStatus,
            run is null ? null : ToCommandRun(run),
            error?.Code,
            string.IsNullOrWhiteSpace(error?.Text)
                ? null
                : error.Text,
            messages);
    }

    private static WorkflowCommandRun ToCommandRun(WorkflowRunSnapshot snapshot)
        => new(
            snapshot.Id,
            snapshot.DefinitionName,
            snapshot.Status,
            snapshot.Steps.Select(ToCommandStep).ToArray(),
            snapshot.CreatedAt,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.Messages);

    private static WorkflowCommandStep ToCommandStep(WorkflowStepRunSnapshot snapshot)
        => new(
            snapshot.Name,
            snapshot.Kind,
            snapshot.Status,
            snapshot.WorkerIds,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.Messages);

    private static WorkflowCommandStatus ToCommandStatus(WorkflowStartStatus status)
        => status switch
        {
            WorkflowStartStatus.Accepted => WorkflowCommandStatus.Accepted,
            WorkflowStartStatus.Invalid => WorkflowCommandStatus.Invalid,
            WorkflowStartStatus.Unauthorized => WorkflowCommandStatus.Unauthorized,
            WorkflowStartStatus.NotFound => WorkflowCommandStatus.NotFound,
            _ => WorkflowCommandStatus.Invalid,
        };

    private static WorkflowCommandStatus ToCommandStatus(WorkflowRunStatus status)
        => status switch
        {
            WorkflowRunStatus.Running => WorkflowCommandStatus.Running,
            WorkflowRunStatus.Paused => WorkflowCommandStatus.Paused,
            WorkflowRunStatus.Blocked => WorkflowCommandStatus.Blocked,
            WorkflowRunStatus.Completed => WorkflowCommandStatus.Completed,
            WorkflowRunStatus.Failed => WorkflowCommandStatus.Failed,
            WorkflowRunStatus.Canceled => WorkflowCommandStatus.Canceled,
            WorkflowRunStatus.Invalid => WorkflowCommandStatus.Invalid,
            WorkflowRunStatus.NotFound => WorkflowCommandStatus.NotFound,
            WorkflowRunStatus.Unauthorized => WorkflowCommandStatus.Unauthorized,
            _ => WorkflowCommandStatus.Invalid,
        };

    private static WorkflowCommandStatus ToCommandStatus(WorkflowActionStatus status)
        => status switch
        {
            WorkflowActionStatus.Accepted => WorkflowCommandStatus.Accepted,
            WorkflowActionStatus.NotFound => WorkflowCommandStatus.NotFound,
            WorkflowActionStatus.Unauthorized => WorkflowCommandStatus.Unauthorized,
            WorkflowActionStatus.Invalid => WorkflowCommandStatus.Invalid,
            _ => WorkflowCommandStatus.Invalid,
        };

    private static WorkflowAction ToWorkflowAction(WorkflowRunAction action)
        => action switch
        {
            WorkflowRunAction.Start => WorkflowAction.Start,
            WorkflowRunAction.Pause => WorkflowAction.Pause,
            WorkflowRunAction.Cancel => WorkflowAction.Cancel,
            _ => WorkflowAction.Cancel,
        };
}
