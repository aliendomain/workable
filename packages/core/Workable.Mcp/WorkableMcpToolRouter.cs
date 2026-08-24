using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Workable;

/// <summary>
/// Routes protocol-facing MCP tool discovery and invocation to Workable systems.
/// </summary>
public sealed class WorkableMcpToolRouter(
    IWorkSystemRegistry registry,
    ILogger<WorkableMcpToolRouter>? logger)
{
    private readonly ILogger<WorkableMcpToolRouter> effectiveLogger = logger ?? NullLogger<WorkableMcpToolRouter>.Instance;
    private const string WorkToolNamePrefix = "workable_work_";
    private const string WorkToolNameBase = "workable_work";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static readonly WorkflowRunViewAdapter WorkflowViews = new();

    private const string QueryWorkersTool = "workable_query_workers";
    private const string GetWorkerTool = "workable_get_worker";
    private const string GetWorkerIterationTool = "workable_get_worker_iteration";
    private const string QueryWorkerIterationsTool = "workable_query_worker_iterations";
    private const string GetWorkInfoTool = "workable_get_work_info";
    private const string QueryWorkDefinitionsTool = "workable_query_work_definitions";
    private const string QueryWorkerKeysTool = "workable_query_worker_keys";
    private const string QueryWorkerKeyTypesTool = "workable_query_worker_key_types";
    private const string QueryWorkIterationKeysTool = "workable_query_work_iteration_keys";
    private const string QueryWorkIterationKeyTypesTool = "workable_query_work_iteration_key_types";
    private const string GetWorkerStatusSummaryTool = "workable_get_worker_status_summary";
    private const string QueryWorkflowRunsTool = "workable_query_workflow_runs";
    private const string GetWorkflowRunTool = "workable_get_workflow_run";
    private const string QueryExecutionDiagnosticsTool = "workable_query_execution_diagnostics";
    private const string GetExecutionDiagnosticTool = "workable_get_execution_diagnostic";
    private const string StartWorkflowTool = "workable_start_workflow";
    private const string StartWorkflowRunTool = "workable_start_workflow_run";
    private const string PauseWorkflowTool = "workable_pause_workflow_run";
    private const string StopWorkflowTool = "workable_stop_workflow";
    private const string CancelWorkflowTool = "workable_cancel_workflow";
    private const string StartWorkerTool = "workable_start_worker";
    private const string PauseWorkerTool = "workable_pause_worker";
    private const string CancelWorkerTool = "workable_cancel_worker";
    private const string PushWorkerTool = "workable_push_worker";
    private const string PurgeWorkerTool = "workable_purge_worker";
    private const string ReconfigureWorkDefinitionTool = "workable_reconfigure_work_definition";

    private static readonly IReadOnlyDictionary<string, QueryToolKind> QueryToolKinds =
        new Dictionary<string, QueryToolKind>(StringComparer.Ordinal)
        {
            [QueryWorkersTool] = QueryToolKind.QueryWorkers,
            [GetWorkerTool] = QueryToolKind.GetWorker,
            [GetWorkerIterationTool] = QueryToolKind.GetWorkerIteration,
            [QueryWorkerIterationsTool] = QueryToolKind.QueryWorkerIterations,
            [GetWorkInfoTool] = QueryToolKind.GetWorkInfo,
            [QueryWorkDefinitionsTool] = QueryToolKind.QueryWorkDefinitions,
            [QueryWorkerKeysTool] = QueryToolKind.QueryWorkerKeys,
            [QueryWorkerKeyTypesTool] = QueryToolKind.QueryWorkerKeyTypes,
            [QueryWorkIterationKeysTool] = QueryToolKind.QueryWorkIterationKeys,
            [QueryWorkIterationKeyTypesTool] = QueryToolKind.QueryWorkIterationKeyTypes,
            [GetWorkerStatusSummaryTool] = QueryToolKind.GetWorkerStatusSummary,
            [QueryWorkflowRunsTool] = QueryToolKind.QueryWorkflowRuns,
            [GetWorkflowRunTool] = QueryToolKind.GetWorkflowRun,
            [QueryExecutionDiagnosticsTool] = QueryToolKind.QueryExecutionDiagnostics,
            [GetExecutionDiagnosticTool] = QueryToolKind.GetExecutionDiagnostic,
        };

    private static readonly IReadOnlyDictionary<string, ActionToolKind> ActionToolKinds =
        new Dictionary<string, ActionToolKind>(StringComparer.Ordinal)
        {
            [StartWorkflowTool] = ActionToolKind.StartWorkflow,
            [StartWorkflowRunTool] = ActionToolKind.StartWorkflowRun,
            [PauseWorkflowTool] = ActionToolKind.PauseWorkflow,
            [StopWorkflowTool] = ActionToolKind.StopWorkflow,
            [CancelWorkflowTool] = ActionToolKind.CancelWorkflow,
            [StartWorkerTool] = ActionToolKind.StartWorker,
            [PauseWorkerTool] = ActionToolKind.PauseWorker,
            [CancelWorkerTool] = ActionToolKind.CancelWorker,
            [PushWorkerTool] = ActionToolKind.PushWorker,
            [PurgeWorkerTool] = ActionToolKind.PurgeWorker,
            [ReconfigureWorkDefinitionTool] = ActionToolKind.ReconfigureWorkDefinition,
        };

    private static readonly IReadOnlyDictionary<string, Func<WorkOperationAccessSummary, bool>> ActionToolAccessors =
        new Dictionary<string, Func<WorkOperationAccessSummary, bool>>(StringComparer.Ordinal)
        {
            [StartWorkflowTool] = static access => access.CanStartWorkflow,
            [StartWorkflowRunTool] = static access => access.CanResumeWorkflow,
            [PauseWorkflowTool] = static access => access.CanPauseWorkflow,
            [StopWorkflowTool] = static access => access.CanPauseWorkflow,
            [CancelWorkflowTool] = static access => access.CanCancelWorkflow,
            [StartWorkerTool] = static access => access.CanStartWorker,
            [PauseWorkerTool] = static access => access.CanPauseWorker,
            [CancelWorkerTool] = static access => access.CanCancelWorker,
            [PushWorkerTool] = static access => access.CanPushWorker,
            [PurgeWorkerTool] = static access => access.CanPurgeWorker,
            [ReconfigureWorkDefinitionTool] = static access => access.CanReconfigureDefinition,
        };

    /// <summary>
    /// Creates a tool router without an optional logger.
    /// </summary>
    /// <param name="registry">The registered Workable systems.</param>
    public WorkableMcpToolRouter(IWorkSystemRegistry registry)
        : this(registry, logger: null)
    {
    }

    /// <summary>
    /// Gets the protocol-facing MCP tools visible to the caller for the selected system.
    /// </summary>
    /// <param name="requestContext">The caller context used to authorize tool visibility.</param>
    /// <param name="options">Optional server settings that control which tool categories are exposed.</param>
    /// <param name="systemName">The Workable system name to expose, or <see langword="null"/> for the default unnamed system.</param>
    /// <returns>
    /// The MCP tools visible to the caller, or an empty list when a requested named system is unknown
    /// or inaccessible to that caller.
    /// </returns>
    public async ValueTask<IReadOnlyList<WorkableMcpServerToolDescriptor>> GetTools(
        WorkRequestContext requestContext,
        WorkableMcpServerOptions? options = null,
        string? systemName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        options ??= WorkableMcpServerOptions.Default;
        var tools = new List<WorkableMcpServerToolDescriptor>();
        if (!TryResolveSystem(systemName, out var system, out _) ||
            !await CanAccessNamedSystem(system, systemName, requestContext, cancellationToken))
        {
            return [];
        }

        var session = await system.CreateSession(requestContext, cancellationToken);
        var access = options.IncludeQueryTools
            ? await system.DescribeAccess(requestContext, cancellationToken)
            : null;
        var operationAccess = options.IncludeActionTools
            ? await DescribeOperationAccess(system, requestContext, cancellationToken)
            : null;

        if (options.IncludeWorkTools)
        {
            tools.AddRange(CreateWorkTools(session, options.ToolCatalog));
        }

        if (options.IncludeQueryTools)
        {
            tools.AddRange(CreateQueryTools(system, access!));
        }

        if (options.IncludeActionTools)
        {
            tools.AddRange(CreateActionTools(operationAccess!));
        }

        return [.. tools.OrderBy(tool => tool.Kind).ThenBy(tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Invokes one protocol-facing MCP tool against the selected system.
    /// </summary>
    /// <param name="toolName">The protocol-safe MCP tool name to invoke.</param>
    /// <param name="arguments">The optional JSON argument payload supplied by the MCP client.</param>
    /// <param name="options">Optional server settings that control which tool categories are exposed and how work tools invoke.</param>
    /// <param name="systemName">The Workable system name to target, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="requestContext">The caller context used for authorization and recorded origin metadata.</param>
    /// <param name="cancellationToken">A token that cancels the invocation.</param>
    /// <returns>
    /// The protocol-facing tool result. Unknown and inaccessible named systems both return the same
    /// system-not-found result.
    /// </returns>
    public async Task<WorkableMcpToolResult> CallTool(
        string toolName,
        JsonElement? arguments,
        WorkableMcpServerOptions? options,
        string? systemName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(requestContext);

        options ??= WorkableMcpServerOptions.Default;
        try
        {
            if (!TryResolveSystem(systemName, out var system, out var systemError))
            {
                return systemError;
            }

            if (!await CanAccessNamedSystem(system, systemName, requestContext, cancellationToken))
            {
                return SystemNotFound(systemName);
            }

            var session = await system.CreateSession(requestContext, cancellationToken);
            var workTools = options.IncludeWorkTools
                ? CreateWorkTools(session, options.ToolCatalog)
                : [];
            var access = options.IncludeQueryTools
                ? await system.DescribeAccess(requestContext, cancellationToken)
                : null;
            var operationAccess = options.IncludeActionTools
                ? await DescribeOperationAccess(system, requestContext, cancellationToken)
                : null;
            var queryTools = options.IncludeQueryTools
                ? CreateQueryTools(system, access!)
                : [];
            var actionTools = options.IncludeActionTools
                ? CreateActionTools(operationAccess!)
                : [];

            if (TryGetWorkToolName(workTools, toolName, out var workName))
            {
                var invocationSession = await system.CreateSession(WithDescription(
                        requestContext,
                        ReadWorkToolInvocationDescription(arguments)),
                    cancellationToken);
                var invocation = await invocationSession.InvokeMcpTool(
                        workName,
                        ReadWorkToolInvocationInput(arguments),
                        options.Invocation,
                        cancellationToken);
                return ToToolResult(invocation, invocation.Status == WorkableMcpInvocationStatus.Rejected);
            }

            if (ContainsTool(queryTools, toolName))
            {
                var queryToolKind = QueryToolKinds[toolName];
                switch (queryToolKind)
                {
                    case QueryToolKind.QueryWorkers:
                        return ToToolResult(await session.Query.Workers(ToWorkerCriteria(arguments), cancellationToken: cancellationToken));
                    case QueryToolKind.GetWorker:
                    {
                        var workerId = ReadRequiredGuid(arguments, "workerId");
                        var worker = await session.Query.Worker(new WorkerId(workerId), cancellationToken: cancellationToken);
                        return ToToolResult(worker is null
                            ? new { found = false, workerId = workerId.ToString("D") }
                            : new { found = true, worker });
                    }
                    case QueryToolKind.GetWorkerIteration:
                    {
                        var workerId = new WorkerId(ReadRequiredGuid(arguments, "workerId"));
                        var sequence = ReadRequiredLong(arguments, "sequence");
                        var iteration = await session.Query.WorkerIteration(
                            new WorkerIterationReference(workerId, sequence),
                            cancellationToken: cancellationToken);
                        return ToToolResult(iteration is null
                            ? new { found = false, workerId = workerId.Value.ToString("D"), sequence }
                            : new { found = true, iteration });
                    }
                    case QueryToolKind.QueryWorkerIterations:
                        return ToToolResult(await session.Query.WorkerIterations(ToWorkerIterationCriteria(arguments), cancellationToken: cancellationToken));
                    case QueryToolKind.QueryExecutionDiagnostics:
                    {
                        var diagnostics = await ResolveExecutionDiagnostics(
                            system,
                            requestContext,
                            cancellationToken);
                        var take = ReadInt(arguments, "take") ?? WorkExecutionDiagnosticCriteria.DefaultTake;
                        if (take is <= 0 or > WorkExecutionDiagnosticCriteria.MaximumTake)
                        {
                            throw new WorkableMcpInvalidArgumentsException(
                                $"Execution diagnostic query take must be between 1 and {WorkExecutionDiagnosticCriteria.MaximumTake}.");
                        }

                        return ToToolResult(await diagnostics.QueryExecutionDiagnostics(
                            new WorkExecutionDiagnosticCriteria(
                                system.Id,
                                DefinitionName: ReadString(arguments, "definitionName") ?? ReadString(arguments, "name"),
                                WorkerId: ReadGuid(arguments, "workerId") is { } workerId ? new WorkerId(workerId) : null,
                                CompletedAfter: ReadDateTimeOffset(arguments, "completedAfter"),
                                CompletedBefore: ReadDateTimeOffset(arguments, "completedBefore"),
                                MinimumLogLevel: ReadOptionalEnum<LogLevel>(arguments, "minimumLogLevel"),
                                Take: take),
                            cancellationToken));
                    }
                    case QueryToolKind.GetExecutionDiagnostic:
                    {
                        var diagnostics = await ResolveExecutionDiagnostics(
                            system,
                            requestContext,
                            cancellationToken);
                        var workerId = new WorkerId(ReadRequiredGuid(arguments, "workerId"));
                        var sequence = ReadRequiredLong(arguments, "sequence");
                        var artifact = await diagnostics.GetExecutionDiagnostic(
                            new WorkExecutionDiagnosticGetRequest(system.Id, workerId, sequence),
                            cancellationToken);
                        return ToToolResult(artifact is null
                            ? new { found = false, workerId = workerId.Value.ToString("D"), sequence }
                            : new { found = true, artifact });
                    }
                    case QueryToolKind.GetWorkInfo:
                    {
                        var name = ReadString(arguments, "name");
                        var info = !string.IsNullOrWhiteSpace(name)
                            ? await session.Query.WorkInfo(name, cancellationToken: cancellationToken)
                            : null;

                        return ToToolResult(info is null
                            ? new { found = false, name }
                            : new { found = true, info });
                    }
                    case QueryToolKind.QueryWorkDefinitions:
                    {
                        var query = new WorkDefinitionCriteria(
                            Name: ReadString(arguments, "name"),
                            Category: ReadString(arguments, "category"),
                            Search: ReadString(arguments, "search"),
                            IncludeSubcategories: ReadBool(arguments, "includeSubcategories") ?? true);
                        return ToToolResult((await session.Query.WorkDefinitions(query, cancellationToken: cancellationToken)).Definitions);
                    }
                    case QueryToolKind.QueryWorkerKeys:
                        return ToToolResult(await session.Query.WorkerKeys(
                            new WorkerKeyCriteria(
                                Kind: ReadOptionalEnum<WorkKeyKind>(arguments, "kind"),
                                Type: ReadString(arguments, "type"),
                                Value: ReadString(arguments, "value"),
                                Search: ReadString(arguments, "search"),
                                States: ReadStates(arguments),
                                Skip: ReadInt(arguments, "skip") ?? 0,
                                Take: ReadInt(arguments, "take") ?? WorkerKeyCriteria.DefaultTake),
                            cancellationToken: cancellationToken));
                    case QueryToolKind.QueryWorkerKeyTypes:
                        return ToToolResult(await session.Query.WorkerKeyTypes(
                            new WorkerKeyTypeCriteria(
                                Kind: ReadOptionalEnum<WorkKeyKind>(arguments, "kind"),
                                Search: ReadString(arguments, "search"),
                                Type: ReadString(arguments, "type"),
                                States: ReadStates(arguments),
                                Skip: ReadInt(arguments, "skip") ?? 0,
                                Take: ReadInt(arguments, "take") ?? WorkerKeyCriteria.DefaultTake),
                            cancellationToken: cancellationToken));
                    case QueryToolKind.QueryWorkIterationKeys:
                        return ToToolResult(await session.Query.WorkIterationKeys(
                            new WorkIterationKeyCriteria(
                                Kind: ReadOptionalEnum<WorkKeyKind>(arguments, "kind"),
                                Type: ReadString(arguments, "type"),
                                Value: ReadString(arguments, "value"),
                                Search: ReadString(arguments, "search"),
                                Statuses: ReadCompletionStatuses(arguments),
                                Skip: ReadInt(arguments, "skip") ?? 0,
                                Take: ReadInt(arguments, "take") ?? WorkIterationKeyCriteria.DefaultTake),
                            cancellationToken: cancellationToken));
                    case QueryToolKind.QueryWorkIterationKeyTypes:
                        return ToToolResult(await session.Query.WorkIterationKeyTypes(
                            new WorkIterationKeyTypeCriteria(
                                Kind: ReadOptionalEnum<WorkKeyKind>(arguments, "kind"),
                                Search: ReadString(arguments, "search"),
                                Type: ReadString(arguments, "type"),
                                Statuses: ReadCompletionStatuses(arguments),
                                Skip: ReadInt(arguments, "skip") ?? 0,
                                Take: ReadInt(arguments, "take") ?? WorkIterationKeyCriteria.DefaultTake),
                            cancellationToken: cancellationToken));
                    case QueryToolKind.GetWorkerStatusSummary:
                        return ToToolResult(await session.Query.WorkerStatusSummary(ToWorkerCriteria(arguments), cancellationToken: cancellationToken));
                    case QueryToolKind.QueryWorkflowRuns:
                        return ToToolResult(await WorkflowViews.RunsPage(
                            system,
                            requestContext,
                            includeFinal: ReadBool(arguments, "includeFinal") ?? false,
                            definitionName: ReadString(arguments, "definitionName") ?? ReadString(arguments, "name"),
                            childSampleSize: ReadChildSampleSize(arguments),
                            skip: ReadWorkflowRunSkip(arguments),
                            take: ReadWorkflowRunTake(arguments),
                            cancellationToken: cancellationToken));
                    case QueryToolKind.GetWorkflowRun:
                    {
                        var runId = new WorkflowRunId(ReadRequiredGuid(arguments, "runId"));
                        var run = await WorkflowViews.Run(
                            system,
                            requestContext,
                            runId,
                            ReadChildSampleSize(arguments),
                            cancellationToken);
                        return ToToolResult(run is null
                            ? new { found = false, runId = runId.Value.ToString("D") }
                            : new { found = true, run });
                    }
                }
            }

            if (ContainsTool(actionTools, toolName))
            {
                var actionToolKind = ActionToolKinds[toolName];
                switch (actionToolKind)
                {
                    case ActionToolKind.StartWorkflow:
                    {
                        var workflowName = ReadRequiredString(arguments, "name");
                        var workflowRequestContext = WithDescription(requestContext, ReadString(arguments, "description"));
                        var runtime = ResolveWorkflowRuntime(system);
                        var handle = await runtime.Start(
                            workflowName,
                            workflowRequestContext,
                            ReadWorkflowStartInput(arguments),
                            cancellationToken);
                        if (!handle.StartOutcome.IsAccepted)
                        {
                            return ToToolResult(new
                            {
                                status = handle.StartOutcome.Status,
                                runId = (string?)null,
                                run = (WorkflowRunSnapshot?)null,
                                messages = handle.StartOutcome.Messages,
                            }, isError: true);
                        }

                        var waitForCompletion = ReadBool(arguments, "waitForCompletion") ?? false;
                        if (waitForCompletion)
                        {
                            var completion = await handle.WaitForCompletion(cancellationToken);
                            var completedSnapshot = completion.Run ??
                                (handle.RunId is { } completedRunId ? runtime.Get(completedRunId) : null);
                            var visibleRun = completedSnapshot is not null &&
                                await runtime.GetVisible(
                                    completedSnapshot.Id,
                                    workflowRequestContext,
                                    cancellationToken) is not null
                                    ? completedSnapshot
                                    : null;
                            return ToToolResult(new
                            {
                                status = completion.Status,
                                runId = handle.RunId?.Value.ToString("D"),
                                run = visibleRun,
                                messages = WorkMessageAccessFilter.Apply(
                                    completion.Messages,
                                    canReadRetainedDetails: visibleRun is not null),
                            });
                        }

                        var acceptedSnapshot = handle.RunId is { } acceptedRunId
                            ? runtime.Get(acceptedRunId)
                            : null;
                        var acceptedRun = acceptedSnapshot is not null &&
                            await runtime.GetVisible(
                                acceptedSnapshot.Id,
                                workflowRequestContext,
                                cancellationToken) is not null
                                ? acceptedSnapshot
                                : null;

                        return ToToolResult(new
                        {
                            status = handle.StartOutcome.Status,
                            runId = handle.RunId?.Value.ToString("D"),
                            run = acceptedRun,
                            messages = handle.StartOutcome.Messages,
                        });
                    }
                    case ActionToolKind.StartWorkflowRun:
                    case ActionToolKind.PauseWorkflow:
                    case ActionToolKind.StopWorkflow:
                    case ActionToolKind.CancelWorkflow:
                    {
                        var runId = new WorkflowRunId(ReadRequiredGuid(arguments, "runId"));
                        var runtime = ResolveWorkflowRuntime(system);
                        var actionRequestContext = WithDescription(requestContext, ReadString(arguments, "description"));
                        var outcome = await runtime.Execute(
                            runId,
                            actionToolKind switch
                            {
                                ActionToolKind.StartWorkflowRun => WorkflowAction.Start,
                                ActionToolKind.PauseWorkflow or ActionToolKind.StopWorkflow => WorkflowAction.Pause,
                                _ => WorkflowAction.Cancel,
                            },
                            actionRequestContext,
                            cancellationToken);
                        return ToToolResult(outcome);
                    }
                    case ActionToolKind.StartWorker:
                    case ActionToolKind.PauseWorker:
                    case ActionToolKind.CancelWorker:
                    case ActionToolKind.PushWorker:
                    case ActionToolKind.PurgeWorker:
                    {
                        var workerId = new WorkerId(ReadRequiredGuid(arguments, "workerId"));
                        var revision = ReadRequiredLong(arguments, "revision");
                        var version = new WorkerVersion(workerId, revision);
                        var action = actionToolKind switch
                        {
                            ActionToolKind.StartWorker => WorkAction.Start,
                            ActionToolKind.PauseWorker => WorkAction.Pause,
                            ActionToolKind.CancelWorker => WorkAction.Cancel,
                            ActionToolKind.PushWorker => WorkAction.Push,
                            _ => WorkAction.Purge,
                        };
                        var actionSession = await system.CreateSession(
                            WithDescription(requestContext, ReadString(arguments, "description")),
                            cancellationToken);
                        return ToToolResult(await actionSession.Workers.Execute(version, action, cancellationToken));
                    }
                    case ActionToolKind.ReconfigureWorkDefinition:
                    {
                        var definitionName = ReadRequiredString(arguments, "name");
                        var revision = ReadRequiredLong(arguments, "revision");
                        var changes = ReadDefinitionReconfiguration(arguments);
                        var reconfigureContext = WithDescription(
                            requestContext,
                            ReadString(arguments, "description"));
                        var reconfigureSession = await system.CreateSession(reconfigureContext, cancellationToken);
                        return ToToolResult(await reconfigureSession.ReconfigureDefinition(
                            definitionName,
                            revision,
                            changes,
                            cancellationToken));
                    }
                }
            }

            return UnknownTool(toolName);
        }
        catch (WorkSystemAccessDeniedException denied)
        {
            return AuthorizationDenied(denied);
        }
        catch (WorkableMcpInvalidArgumentsException invalidArguments)
        {
            return InvalidArguments(invalidArguments.Message);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or
            OutOfMemoryException or
            StackOverflowException))
        {
            this.effectiveLogger.LogError(exception, "Failed to invoke Workable MCP tool '{ToolName}'.", toolName);
            return ToolFailure();
        }
    }

    /// <summary>
    /// Converts a Workable definition name into the normalized MCP-safe tool name used by the protocol-facing server surface.
    /// </summary>
    /// <param name="workName">The original Workable definition name.</param>
    /// <returns>The normalized MCP-safe tool name.</returns>
    public static string CreateWorkToolName(string workName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workName);

        var builder = new StringBuilder(WorkToolNamePrefix);
        var previousWasSeparator = false;
        foreach (var character in workName)
        {
            if (char.IsAsciiLetterOrDigit(character) || character == '_')
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        var toolName = builder.ToString().TrimEnd('_');
        return LimitToolName(toolName.Length == WorkToolNameBase.Length ? WorkToolNameBase : toolName);
    }

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateWorkTools(
        IWorkSystemSession session,
        WorkableMcpToolCatalogOptions catalogOptions)
    {
        var descriptors = session.GetMcpToolDescriptors(catalogOptions);
        var baseNameCounts = descriptors
            .GroupBy(descriptor => CreateWorkToolName(descriptor.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return [.. descriptors.Select(descriptor =>
        {
            var baseName = CreateWorkToolName(descriptor.Name);
            var toolName = baseNameCounts[baseName] == 1
                ? baseName
                : LimitToolName(baseName, CreateNameSuffix(descriptor.Name));

            return new WorkableMcpServerToolDescriptor(
                toolName,
                CreateWorkToolDescription(descriptor),
                CreateWorkToolInputSchema(descriptor.InputSchemaJson),
                descriptor.OutputSchemaJson,
                WorkableMcpServerToolKind.Work,
                descriptor.Name);
        })];
    }

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateQueryTools(
        IWorkSystem system,
        WorkSystemAccessSummary access)
    {
        var canReadWork = access.CanReadAllWork || access.ReadableDefinitionCount > 0;
        var canReadWorkflows = access.CanReadAllWork || access.ReadableWorkflowDefinitionCount > 0;
        var tools = new List<WorkableMcpServerToolDescriptor>
        {
            new(
                QueryWorkersTool,
                "Find workers by work name, state, subject, concurrency key, identifier, selected configuration flags, time range, and paging. Use this before worker actions when you need the current worker id and revision.",
                WorkerQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                GetWorkerTool,
                "Get one worker snapshot by worker id, including current state, revision, output, messages, logs, profile, action history, and retained recurrence iterations.",
                """{"type":"object","properties":{"workerId":{"type":"string"}},"required":["workerId"],"additionalProperties":false}""",
                null,
                WorkableMcpServerToolKind.Query),
            new(
                GetWorkerIterationTool,
                "Get one worker iteration by worker id and iteration sequence, including output, messages, logs, profile, and timing.",
                """{"type":"object","properties":{"workerId":{"type":"string"},"sequence":{"type":"integer"}},"required":["workerId","sequence"],"additionalProperties":false}""",
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkerIterationsTool,
                "Find worker iterations by worker id, work name, status, subject, concurrency key, identifier, category, time range, and paging. Use this for recent execution history, failures, transient retries, and recurring iteration activity.",
                WorkerIterationQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                GetWorkInfoTool,
                "Get one work definition plus worker rollup counts by work name. Use this to understand whether a kind of work is healthy or active.",
                """{"type":"object","properties":{"name":{"type":"string"}},"additionalProperties":false}""",
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkDefinitionsTool,
                "Browse available work definitions by name, category, or search text. Use this to discover what work can be queued.",
                WorkDefinitionQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkerKeysTool,
                "Search known worker keys across subjects, concurrency keys, and identifiers by kind, type, value, free text, and optional worker states. Use this when a user asks for workers tied to something like claim id 123, a customer, or an order, including phrases like currently running claim work. Returns matching keys with their worker overview rows.",
                WorkKeyQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkerKeyTypesTool,
                "List known worker key types across subjects, concurrency keys, and identifiers, optionally filtered by worker state. Use this when a user asks for broad worker categories like claim work or customer work. Results are grouped by type across key kinds and include worker overview rows for workers attached to those key types.",
                WorkKeyTypeQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkIterationKeysTool,
                "Search known work iteration keys across subjects, concurrency keys, and identifiers by kind, type, value, free text, and optional iteration statuses. Use this when a user asks for actual executions tied to something like claim id 123, failed customer work, or completed order work. Returns matching keys with their iteration overview rows.",
                WorkIterationKeyQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkIterationKeyTypesTool,
                "List known work iteration key types across subjects, concurrency keys, and identifiers, optionally filtered by iteration status. Use this for broad execution categories like claim work, customer work, or failed order activity. Results are grouped by type across key kinds and include iteration overview rows for iterations attached to those key types.",
                WorkIterationKeyTypeQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                GetWorkerStatusSummaryTool,
                "Get worker counts by state for the whole system or a filtered worker query. Use this for a quick activity/status summary.",
                WorkerQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkflowRunsTool,
                "List visible workflow runs for operator-style monitoring. By default this returns currently executing runs and can optionally include final runs or filter by workflow definition name.",
                WorkflowRunQuerySchema,
                null,
                WorkableMcpServerToolKind.Query),
            new(
                GetWorkflowRunTool,
                "Get one workflow run detail view, including step graph state and child-worker summaries suitable for operator drilldown.",
                WorkflowRunGetSchema,
                null,
                WorkableMcpServerToolKind.Query),
        };
        tools.RemoveAll(tool =>
            (IsWorkQueryTool(tool.ToolName) && !canReadWork) ||
            (IsWorkflowQueryTool(tool.ToolName) && !canReadWorkflows));

        if (access.CanViewDiagnostics &&
            system is IWorkExecutionDiagnosticsSystem
            {
                ExecutionDiagnosticsPersistenceAvailable: true,
            })
        {
            tools.Add(new(
                QueryExecutionDiagnosticsTool,
                "Query persisted work iteration logs and profile summaries. Use this to inspect recent executions and count SQL, HTTP, or other instrumented operations without loading full profile trees. Each result reports whether SQL and HTTP client profiling were available for that execution, so distinguish zero operations from unavailable instrumentation.",
                """{"type":"object","properties":{"definitionName":{"type":"string"},"workerId":{"type":"string"},"completedAfter":{"type":"string","format":"date-time"},"completedBefore":{"type":"string","format":"date-time"},"minimumLogLevel":{"type":"string","enum":["Trace","Debug","Information","Warning","Error","Critical"]},"take":{"type":"integer","minimum":1,"maximum":1000}},"additionalProperties":false}""",
                null,
                WorkableMcpServerToolKind.Query));
            tools.Add(new(
                GetExecutionDiagnosticTool,
                "Get persisted logs and the complete profile tree for one worker iteration, including the SQL and HTTP client profiling availability captured for that execution. Use the query tool first when the worker id or iteration sequence is unknown.",
                """{"type":"object","properties":{"workerId":{"type":"string"},"sequence":{"type":"integer"}},"required":["workerId","sequence"],"additionalProperties":false}""",
                null,
                WorkableMcpServerToolKind.Query));
        }

        return tools;
    }

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateActionTools(
        WorkOperationAccessSummary access)
    {
        var tools = new List<WorkableMcpServerToolDescriptor>
        {
            new(
                StartWorkflowTool,
                "Start a registered workflow by name. By default this returns the accepted run id and current snapshot immediately, and can optionally wait for workflow completion.",
                WorkflowStartSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                StartWorkflowRunTool,
                "Resume one paused or blocked workflow run by run id. This continues the existing run instead of creating a new one.",
                WorkflowActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                PauseWorkflowTool,
                "Pause a running workflow run and pause any outstanding child workers that can be paused. The run remains resumable.",
                WorkflowActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                StopWorkflowTool,
                "Pause a running workflow run. This tool name is retained for compatibility and behaves the same as workable_pause_workflow_run.",
                WorkflowActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                CancelWorkflowTool,
                "Immediately cancel a running workflow and request cancellation for any outstanding child workers that can still be canceled.",
                WorkflowActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                StartWorkerTool,
                "Start or retry a worker that is queued, paused, or failed. Requires the current worker id and revision from get/query worker to avoid conflicting with another caller.",
                WorkerActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                PauseWorkerTool,
                "Pause a queued, running, waiting, or retrying worker. Requires the current worker id and revision.",
                WorkerActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                CancelWorkerTool,
                "Permanently stop a non-final worker. Canceled work cannot be restarted. Requires the current worker id and revision.",
                WorkerActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                PushWorkerTool,
                "Skip the current recurrence wait and begin the next iteration immediately. Only valid for a recurring worker in the waiting state. Requires the current worker id and revision.",
                WorkerActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                PurgeWorkerTool,
                "Remove a completed or canceled worker from memory permanently. Use only when the worker is final and no longer needs to be queried. Requires the current worker id and revision.",
                WorkerActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                ReconfigureWorkDefinitionTool,
                "Change a work definition's default worker options and/or default configuration for future queued workers. Requires the current work name and revision from query_work_definitions or get_work_info.",
                WorkDefinitionReconfigurationSchema,
                null,
                WorkableMcpServerToolKind.Action),
        };

        tools.RemoveAll(tool => !ActionToolAccessors[tool.ToolName](access));
        return tools;
    }

    private enum QueryToolKind
    {
        QueryWorkers,
        GetWorker,
        GetWorkerIteration,
        QueryWorkerIterations,
        GetWorkInfo,
        QueryWorkDefinitions,
        QueryWorkerKeys,
        QueryWorkerKeyTypes,
        QueryWorkIterationKeys,
        QueryWorkIterationKeyTypes,
        GetWorkerStatusSummary,
        QueryWorkflowRuns,
        GetWorkflowRun,
        QueryExecutionDiagnostics,
        GetExecutionDiagnostic,
    }

    private enum ActionToolKind
    {
        StartWorkflow,
        StartWorkflowRun,
        PauseWorkflow,
        StopWorkflow,
        CancelWorkflow,
        StartWorker,
        PauseWorker,
        CancelWorker,
        PushWorker,
        PurgeWorker,
        ReconfigureWorkDefinition,
    }

    private static bool ContainsTool(
        IReadOnlyList<WorkableMcpServerToolDescriptor> tools,
        string toolName)
        => tools.Any(tool => string.Equals(tool.ToolName, toolName, StringComparison.Ordinal));

    private static bool IsWorkQueryTool(string toolName)
        => toolName is
            QueryWorkersTool or
            GetWorkerTool or
            GetWorkerIterationTool or
            QueryWorkerIterationsTool or
            GetWorkInfoTool or
            QueryWorkDefinitionsTool or
            QueryWorkerKeysTool or
            QueryWorkerKeyTypesTool or
            QueryWorkIterationKeysTool or
            QueryWorkIterationKeyTypesTool or
            GetWorkerStatusSummaryTool;

    private static bool IsWorkflowQueryTool(string toolName)
        => toolName is QueryWorkflowRunsTool or GetWorkflowRunTool;

    private static async ValueTask<WorkOperationAccessSummary> DescribeOperationAccess(
        IWorkSystem system,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
        => system is IWorkOperationAccessSource operationAccess
            ? await operationAccess.DescribeOperationAccess(requestContext, cancellationToken)
            : WorkOperationAccessSummary.FromSystemWideAccess(
                await system.DescribeAccess(requestContext, cancellationToken));

    private static WorkerCriteria ToWorkerCriteria(JsonElement? arguments)
    {
        return new WorkerCriteria(
            DefinitionName: ReadString(arguments, "definitionName") ?? ReadString(arguments, "workName"),
            SubjectId: ReadPair(arguments, "subjectType", "subjectValue") is { } subject ? new WorkSubjectId(subject.Type, subject.Value) : null,
            ConcurrencyKey: ReadPair(arguments, "concurrencyKeyType", "concurrencyKeyValue") is { } key ? new WorkConcurrencyKey(key.Type, key.Value) : null,
            Identifier: ReadPair(arguments, "identifierType", "identifierValue") is { } identifier ? new WorkIdentifier(identifier.Type, identifier.Value) : null,
            States: ReadStates(arguments),
            Configuration: ReadWorkerConfigurationQuery(arguments),
            CreatedFrom: ReadDateTimeOffset(arguments, "createdFrom"),
            CreatedTo: ReadDateTimeOffset(arguments, "createdTo"),
            UpdatedFrom: ReadDateTimeOffset(arguments, "updatedFrom"),
            UpdatedTo: ReadDateTimeOffset(arguments, "updatedTo"),
            Sort: ReadEnum(arguments, "sort", WorkerCriteriaSort.CreatedAt),
            Direction: ReadEnum(arguments, "direction", WorkCriteriaSortDirection.Descending),
            Skip: ReadInt(arguments, "skip") ?? 0,
            Take: ReadInt(arguments, "take") ?? 100,
            Category: ReadString(arguments, "category"),
            IncludeSubcategories: ReadBool(arguments, "includeSubcategories") ?? true);
    }

    private static WorkerConfigurationCriteria? ReadWorkerConfigurationQuery(JsonElement? arguments)
    {
        var recurrenceEnabled = ReadBool(arguments, "recurrenceEnabled");
        var concurrencyEnabled = ReadBool(arguments, "concurrencyEnabled");
        var profilingEnabled = ReadBool(arguments, "profilingEnabled");
        return recurrenceEnabled is null &&
            concurrencyEnabled is null &&
            profilingEnabled is null
            ? null
            : new WorkerConfigurationCriteria(
                RecurrenceEnabled: recurrenceEnabled,
                ConcurrencyEnabled: concurrencyEnabled,
                ProfilingEnabled: profilingEnabled);
    }

    private static WorkerIterationCriteria ToWorkerIterationCriteria(JsonElement? arguments)
    {
        return new WorkerIterationCriteria(
            WorkerId: ReadGuid(arguments, "workerId") is { } workerId ? new WorkerId(workerId) : null,
            DefinitionName: ReadString(arguments, "definitionName") ?? ReadString(arguments, "workName"),
            Category: ReadString(arguments, "category"),
            SubjectId: ReadPair(arguments, "subjectType", "subjectValue") is { } subject ? new WorkSubjectId(subject.Type, subject.Value) : null,
            ConcurrencyKey: ReadPair(arguments, "concurrencyKeyType", "concurrencyKeyValue") is { } key ? new WorkConcurrencyKey(key.Type, key.Value) : null,
            Identifier: ReadPair(arguments, "identifierType", "identifierValue") is { } identifier ? new WorkIdentifier(identifier.Type, identifier.Value) : null,
            Statuses: ReadCompletionStatuses(arguments),
            StartedFrom: ReadDateTimeOffset(arguments, "startedFrom"),
            StartedTo: ReadDateTimeOffset(arguments, "startedTo"),
            CompletedFrom: ReadDateTimeOffset(arguments, "completedFrom"),
            CompletedTo: ReadDateTimeOffset(arguments, "completedTo"),
            Sort: ReadEnum(arguments, "sort", WorkerIterationCriteriaSort.CompletedAt),
            Direction: ReadEnum(arguments, "direction", WorkCriteriaSortDirection.Descending),
            Skip: ReadInt(arguments, "skip") ?? 0,
            Take: ReadInt(arguments, "take") ?? WorkerIterationCriteria.DefaultTake);
    }

    private static bool TryGetWorkToolName(
        IReadOnlyList<WorkableMcpServerToolDescriptor> descriptors,
        string toolName,
        out string workName)
    {
        var descriptor = descriptors
            .Where(descriptor => string.Equals(descriptor.ToolName, toolName, StringComparison.Ordinal))
            .FirstOrDefault();
        if (descriptor is not null)
        {
            workName = descriptor.WorkName!;
            return true;
        }

        workName = string.Empty;
        return false;
    }

    private static string CreateWorkToolDescription(WorkableMcpToolDescriptor descriptor)
    {
        var category = string.IsNullOrWhiteSpace(descriptor.Category)
            ? "uncategorized"
            : descriptor.Category;
        var description = string.IsNullOrWhiteSpace(descriptor.Description)
            ? "No description was provided."
            : descriptor.Description;

        return $"Queue Workable work '{descriptor.Name}' in category '{category}'. {description} The input schema describes the arguments. By default the MCP server waits for completion and returns the work output.";
    }

    private bool TryResolveSystem(
        string? systemName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWorkSystem? system,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out WorkableMcpToolResult? error)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            system = registry.Default;
            error = null;
            return true;
        }

        if (registry.TryGet(systemName, out system))
        {
            error = null;
            return true;
        }

        error = SystemNotFound(systemName);
        return false;
    }

    private static WorkableMcpToolResult SystemNotFound(string? systemName)
        => ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error("workable.mcp.system_not_found", $"Workable system '{systemName}' was not found.", "systemName"),
            },
        }, isError: true);

    private static async ValueTask<bool> CanAccessNamedSystem(
        IWorkSystem system,
        string? systemName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(systemName) ||
            (await system.DescribeAccess(requestContext, cancellationToken)).HasAnyAccess();
    }

    private static WorkflowRuntime ResolveWorkflowRuntime(IWorkSystem system)
        => system is InMemoryWorkSystem inMemory
            ? inMemory.WorkflowRuntime
            : throw new InvalidOperationException("Workflow MCP tools require the built-in Workable system implementation.");

    private static async Task<IWorkExecutionDiagnosticsSystem> ResolveExecutionDiagnostics(
        IWorkSystem system,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!(await system.DescribeAccess(requestContext, cancellationToken)).CanViewDiagnostics)
        {
            throw new WorkSystemAccessDeniedException(
                WorkSystemPermission.ViewDiagnostics,
                system.Id,
                system.Name);
        }

        if (system is not IWorkExecutionDiagnosticsSystem
            {
                ExecutionDiagnosticsPersistenceAvailable: true,
            } diagnostics)
        {
            throw new InvalidOperationException(
                "Persistent execution diagnostics are not available for this system.");
        }

        return diagnostics;
    }

    private static WorkableMcpToolResult ToToolResult(object value, bool isError = false)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return new WorkableMcpToolResult(json, ToJsonObject(json), isError);
    }

    private static WorkableMcpToolResult ToToolResult(WorkActionOutcome outcome)
        => ToToolResult((object)outcome, isError: !outcome.IsAccepted);

    private static WorkableMcpToolResult ToToolResult(WorkflowActionOutcome outcome)
        => ToToolResult((object)outcome, isError: !outcome.IsAccepted);

    private static WorkableMcpToolResult ToToolResult(WorkDefinitionReconfigurationOutcome outcome)
        => ToToolResult((object)outcome, isError: !outcome.IsAccepted);

    private static WorkableMcpToolResult UnknownTool(string toolName)
        => ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error("workable.mcp.tool_not_found", $"MCP tool '{toolName}' was not found.", "toolName"),
            },
        }, isError: true);

    private static WorkableMcpToolResult InvalidArguments(string message)
        => ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error("workable.mcp.arguments_invalid", message, "arguments"),
            },
        }, isError: true);

    private static WorkableMcpToolResult ToolFailure()
        => ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error(
                    "workable.mcp.tool_failed",
                    "The MCP tool could not be completed.",
                    "tool"),
            },
        }, isError: true);

    internal static WorkableMcpToolResult AuthorizationDenied(WorkSystemAccessDeniedException exception)
        => ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error("workable.mcp.authorization_denied", exception.Message, "system"),
            },
        }, isError: true);

    private static string LimitToolName(string baseName, string? suffix = null)
    {
        const int maxToolNameLength = 64;

        if (suffix is null)
        {
            return baseName.Length <= maxToolNameLength
                ? baseName
                : baseName[..maxToolNameLength].TrimEnd('_');
        }

        var suffixLength = suffix.Length + 1;
        var baseLength = Math.Max(1, maxToolNameLength - suffixLength);
        var limitedBase = baseName.Length <= baseLength
            ? baseName
            : baseName[..baseLength].TrimEnd('_');

        return $"{limitedBase}_{suffix}";
    }

    private static WorkRequestContext WithDescription(
        WorkRequestContext requestContext,
        string? description)
        => string.IsNullOrWhiteSpace(description)
            ? requestContext
            : requestContext with
            {
                Description = description,
            };

    private static string? ReadWorkToolInvocationDescription(JsonElement? arguments)
        => TryGetProperty(arguments, "input", out _) ? ReadString(arguments, "description") : null;

    private static JsonElement? ReadWorkToolInvocationInput(JsonElement? arguments)
    {
        if (TryGetProperty(arguments, "input", out var input) &&
            TryGetProperty(arguments, "description", out var description) &&
            description.ValueKind == JsonValueKind.String)
        {
            return input.ValueKind == JsonValueKind.Null
                ? null
                : input;
        }

        return arguments;
    }

    private static WorkInput? ReadWorkflowStartInput(JsonElement? arguments)
    {
        if (!TryGetProperty(arguments, "input", out var input) || input.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return WorkInput.FromJson(input.GetRawText());
        }
        catch (JsonException exception)
        {
            throw new WorkableMcpInvalidArgumentsException("MCP argument 'input' is invalid.", exception);
        }
    }

    private static string CreateWorkToolInputSchema(string inputSchemaJson)
    {
        var inputSchema = JsonNode.Parse(inputSchemaJson)
            ?? throw new InvalidOperationException("Expected an MCP work tool input schema.");
        return new JsonObject
        {
            ["type"] = "object",
            ["oneOf"] = new JsonArray
            {
                inputSchema.DeepClone(),
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["input"] = new JsonObject
                        {
                            ["anyOf"] = new JsonArray
                            {
                                inputSchema.DeepClone(),
                                new JsonObject
                                {
                                    ["type"] = "null",
                                },
                            },
                        },
                        ["description"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional request context stored on the worker origin so callers can attach human-readable intent or audit notes.",
                        },
                    },
                    ["required"] = new JsonArray("input", "description"),
                    ["additionalProperties"] = false,
                },
            },
        }.ToJsonString();
    }

    private static JsonElement? ToJsonObject(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static HashSet<WorkerState>? ReadStates(JsonElement? arguments)
    {
        if (!TryGetProperty(arguments, "states", out var states) || states.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = states.EnumerateArray()
            .Where(static state => state.ValueKind == JsonValueKind.String)
            .Select(static state => TryParseWorkerState(state.GetString()))
            .OfType<WorkerState>()
            .ToHashSet();

        return parsed.Count == 0 ? null : parsed;
    }

    private static HashSet<WorkCompletionStatus>? ReadCompletionStatuses(JsonElement? arguments)
    {
        if (!TryGetProperty(arguments, "statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = statuses.EnumerateArray()
            .Where(static status => status.ValueKind == JsonValueKind.String)
            .Select(static status => TryParseCompletionStatus(status.GetString()))
            .OfType<WorkCompletionStatus>()
            .ToHashSet();

        return parsed.Count == 0 ? null : parsed;
    }

    private static WorkerState? TryParseWorkerState(string? value)
        => Enum.TryParse<WorkerState>(value, ignoreCase: true, out var workerState)
            ? workerState
            : null;

    private static WorkCompletionStatus? TryParseCompletionStatus(string? value)
        => Enum.TryParse<WorkCompletionStatus>(value, ignoreCase: true, out var status)
            ? status
            : null;

    private static Guid ReadRequiredGuid(JsonElement? arguments, string propertyName)
        => ReadGuid(arguments, propertyName) ?? throw new WorkableMcpInvalidArgumentsException($"Required MCP argument '{propertyName}' is missing or invalid.");

    private static Guid? ReadGuid(JsonElement? arguments, string propertyName)
        => Guid.TryParse(ReadString(arguments, propertyName), out var value) ? value : null;

    private static string? ReadString(JsonElement? arguments, string propertyName)
        => TryGetProperty(arguments, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement? arguments, string propertyName)
        => TryGetProperty(arguments, propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long ReadRequiredLong(JsonElement? arguments, string propertyName)
        => TryGetProperty(arguments, propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value
            : throw new WorkableMcpInvalidArgumentsException($"Required MCP argument '{propertyName}' is missing or invalid.");

    private static bool? ReadBool(JsonElement? arguments, string propertyName)
    {
        if (!TryGetProperty(arguments, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null,
        };
    }

    private static int ReadChildSampleSize(JsonElement? arguments)
    {
        var value = ReadInt(arguments, "childSampleSize") ?? 3;
        if (value is < 0 or > WorkflowRunViewAdapter.MaximumChildSampleSize)
        {
            throw new WorkableMcpInvalidArgumentsException(
                $"MCP argument 'childSampleSize' must be between 0 and {WorkflowRunViewAdapter.MaximumChildSampleSize}.");
        }

        return value;
    }

    private static int ReadWorkflowRunSkip(JsonElement? arguments)
    {
        var value = ReadInt(arguments, "skip") ?? 0;
        if (value is < 0 or > WorkflowRunViewAdapter.MaximumRunPageSkip)
        {
            throw new WorkableMcpInvalidArgumentsException(
                $"MCP argument 'skip' must be between 0 and {WorkflowRunViewAdapter.MaximumRunPageSkip}.");
        }

        return value;
    }

    private static int ReadWorkflowRunTake(JsonElement? arguments)
    {
        var value = ReadInt(arguments, "take") ?? 50;
        if (value is < 1 or > WorkflowRunViewAdapter.MaximumRunPageSize)
        {
            throw new WorkableMcpInvalidArgumentsException(
                $"MCP argument 'take' must be between 1 and {WorkflowRunViewAdapter.MaximumRunPageSize}.");
        }

        return value;
    }

    private static WorkDefinitionReconfiguration ReadDefinitionReconfiguration(JsonElement? arguments)
    {
        RejectDuplicatePropertiesRecursively(arguments, "arguments");
        RejectUnsupportedProperties(
            arguments,
            "arguments",
            "name",
            "revision",
            "description",
            "changes",
            "defaultOptions",
            "configuration");
        var hasNestedChanges = TryGetProperty(arguments, "changes", out var nestedChanges);
        var hasTopLevelOptions = TryGetProperty(arguments, "defaultOptions", out var defaultOptions);
        var hasTopLevelConfiguration = TryGetProperty(arguments, "configuration", out var configuration);
        if (hasNestedChanges && (hasTopLevelOptions || hasTopLevelConfiguration))
        {
            throw new WorkableMcpInvalidArgumentsException(
                "MCP reconfiguration arguments must use either 'changes' or top-level 'defaultOptions'/'configuration', not both.");
        }

        if (hasNestedChanges)
        {
            return DeserializeDefinitionReconfiguration(nestedChanges, "changes");
        }

        var changes = new WorkDefinitionReconfiguration(
            DefaultOptions: hasTopLevelOptions
                ? DeserializeRequiredObject<WorkerOptions>(defaultOptions, "defaultOptions")
                : null,
            Configuration: hasTopLevelConfiguration
                ? DeserializeRequiredObject<WorkConfiguration>(configuration, "configuration")
                : null);
        return RequireDefinitionReconfigurationChanges(changes);
    }

    private static WorkDefinitionReconfiguration DeserializeDefinitionReconfiguration(
        JsonElement value,
        string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkableMcpInvalidArgumentsException($"MCP argument '{propertyName}' must be an object.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new WorkableMcpInvalidArgumentsException(
                    $"MCP argument '{propertyName}' contains duplicate property '{property.Name}'.");
            }

            if (property.Name.Equals("defaultOptions", StringComparison.OrdinalIgnoreCase))
            {
                _ = DeserializeRequiredObject<WorkerOptions>(property.Value, $"{propertyName}.defaultOptions");
                continue;
            }

            if (property.Name.Equals("configuration", StringComparison.OrdinalIgnoreCase))
            {
                _ = DeserializeRequiredObject<WorkConfiguration>(property.Value, $"{propertyName}.configuration");
                continue;
            }

            throw new WorkableMcpInvalidArgumentsException(
                $"MCP argument '{propertyName}' contains unsupported property '{property.Name}'.");
        }

        WorkDefinitionReconfiguration changes;
        try
        {
            changes = value.Deserialize<WorkDefinitionReconfiguration>(StrictJsonOptions)!;
        }
        catch (JsonException exception)
        {
            throw new WorkableMcpInvalidArgumentsException($"MCP argument '{propertyName}' is invalid.", exception);
        }

        return RequireDefinitionReconfigurationChanges(changes);
    }

    private static T DeserializeRequiredObject<T>(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkableMcpInvalidArgumentsException($"MCP argument '{propertyName}' must be an object.");
        }

        RejectDuplicatePropertiesRecursively(value, propertyName);
        try
        {
            return value.Deserialize<T>(StrictJsonOptions)!;
        }
        catch (JsonException exception)
        {
            throw new WorkableMcpInvalidArgumentsException($"MCP argument '{propertyName}' is invalid.", exception);
        }
    }

    private static void RejectDuplicatePropertiesRecursively(JsonElement? value, string propertyName)
    {
        if (value is not { } element)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicatePropertiesRecursively(item, $"{propertyName}[{index}]");
                index++;
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new WorkableMcpInvalidArgumentsException(
                    $"MCP argument '{propertyName}' contains duplicate property '{property.Name}'.");
            }

            RejectDuplicatePropertiesRecursively(property.Value, $"{propertyName}.{property.Name}");
        }
    }

    private static void RejectUnsupportedProperties(
        JsonElement? value,
        string propertyName,
        params string[] supportedProperties)
    {
        if (value is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        var supported = supportedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!supported.Contains(property.Name))
            {
                throw new WorkableMcpInvalidArgumentsException(
                    $"MCP argument '{propertyName}' contains unsupported property '{property.Name}'.");
            }
        }
    }

    private static WorkDefinitionReconfiguration RequireDefinitionReconfigurationChanges(
        WorkDefinitionReconfiguration changes)
        => changes.DefaultOptions is not null || changes.Configuration is not null
            ? changes
            : throw new WorkableMcpInvalidArgumentsException(
                "MCP reconfiguration requires at least one of 'defaultOptions' or 'configuration'.");

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement? arguments, string propertyName)
        => DateTimeOffset.TryParse(ReadString(arguments, propertyName), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private static TEnum ReadEnum<TEnum>(JsonElement? arguments, string propertyName, TEnum defaultValue)
        where TEnum : struct
        => Enum.TryParse<TEnum>(ReadString(arguments, propertyName), ignoreCase: true, out var value) ? value : defaultValue;

    private static TEnum? ReadOptionalEnum<TEnum>(JsonElement? arguments, string propertyName)
        where TEnum : struct
        => Enum.TryParse<TEnum>(ReadString(arguments, propertyName), ignoreCase: true, out var value) ? value : null;

    private static (string Type, string Value)? ReadPair(JsonElement? arguments, string typeProperty, string valueProperty)
    {
        var type = ReadString(arguments, typeProperty);
        var value = ReadString(arguments, valueProperty);
        return string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value) ? null : (type, value);
    }

    private static bool TryGetProperty(JsonElement? arguments, string propertyName, out JsonElement property)
    {
        if (arguments is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        property = default;
        return false;
    }

    private static string ReadRequiredString(JsonElement? arguments, string propertyName)
        => ReadString(arguments, propertyName) is { Length: > 0 } value
            ? value
            : throw new WorkableMcpInvalidArgumentsException($"Missing required argument '{propertyName}'.");

    private sealed class WorkableMcpInvalidArgumentsException : Exception
    {
        public WorkableMcpInvalidArgumentsException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    private static string CreateNameSuffix(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    private const string WorkerQuerySchema = """
        {
          "type": "object",
          "properties": {
            "definitionName": { "type": "string" },
            "workName": { "type": "string" },
            "subjectType": { "type": "string" },
            "subjectValue": { "type": "string" },
            "concurrencyKeyType": { "type": "string" },
            "concurrencyKeyValue": { "type": "string" },
            "identifierType": { "type": "string" },
            "identifierValue": { "type": "string" },
            "states": { "type": "array", "items": { "type": "string" } },
            "recurrenceEnabled": {
              "type": "boolean",
              "description": "Filter workers by whether recurrence is enabled in their effective configuration."
            },
            "concurrencyEnabled": {
              "type": "boolean",
              "description": "Filter workers by whether concurrency is enabled in their effective configuration."
            },
            "profilingEnabled": {
              "type": "boolean",
              "description": "Filter workers by whether profiling is enabled in their worker options."
            },
            "createdFrom": { "type": "string", "format": "date-time" },
            "createdTo": { "type": "string", "format": "date-time" },
            "updatedFrom": { "type": "string", "format": "date-time" },
            "updatedTo": { "type": "string", "format": "date-time" },
            "sort": { "type": "string" },
            "direction": { "type": "string" },
            "skip": { "type": "integer" },
            "take": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkerIterationQuerySchema = """
        {
          "type": "object",
          "properties": {
            "workerId": { "type": "string" },
            "definitionName": { "type": "string" },
            "workName": { "type": "string" },
            "category": { "type": "string" },
            "subjectType": { "type": "string" },
            "subjectValue": { "type": "string" },
            "concurrencyKeyType": { "type": "string" },
            "concurrencyKeyValue": { "type": "string" },
            "identifierType": { "type": "string" },
            "identifierValue": { "type": "string" },
            "statuses": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional iteration statuses such as Executing, Completed, Failed, Paused, or Canceled."
            },
            "startedFrom": { "type": "string", "format": "date-time" },
            "startedTo": { "type": "string", "format": "date-time" },
            "completedFrom": { "type": "string", "format": "date-time" },
            "completedTo": { "type": "string", "format": "date-time" },
            "sort": { "type": "string" },
            "direction": { "type": "string" },
            "skip": { "type": "integer" },
            "take": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkDefinitionQuerySchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "category": { "type": "string" },
            "search": { "type": "string" },
            "includeSubcategories": { "type": "boolean" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkKeyQuerySchema = """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "string",
              "description": "Optional key kind: Subject, ConcurrencyKey, or Identifier."
            },
            "type": {
              "type": "string",
              "description": "Exact key type, such as claim, customer, order, tenant, or invoice."
            },
            "value": {
              "type": "string",
              "description": "Exact key value, such as a claim id or customer id."
            },
            "search": {
              "type": "string",
              "description": "Contains search across key type and value. Useful for phrases like claim 123 or claim work."
            },
            "states": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional worker states to keep in each key result, such as Running, Waiting, Completed, Failed, or Canceled."
            },
            "skip": { "type": "integer" },
            "take": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkKeyTypeQuerySchema = """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "string",
              "description": "Optional key kind: Subject, ConcurrencyKey, or Identifier."
            },
            "search": {
              "type": "string",
              "description": "Contains search across key type names. Useful for broad requests like claim work or customer work."
            },
            "type": {
              "type": "string",
              "description": "Exact key type to return across key kinds, such as claim, customer, order, or tenant."
            },
            "states": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional worker states to keep in each key type result, such as Running, Waiting, Completed, Failed, or Canceled."
            },
            "skip": { "type": "integer" },
            "take": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkIterationKeyQuerySchema = """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "string",
              "description": "Optional key kind: Subject, ConcurrencyKey, or Identifier."
            },
            "type": {
              "type": "string",
              "description": "Exact key type, such as claim, customer, order, tenant, or invoice."
            },
            "value": {
              "type": "string",
              "description": "Exact key value, such as a claim id or customer id."
            },
            "search": {
              "type": "string",
              "description": "Contains search across key type and value. Useful for phrases like claim 123 or failed claim work."
            },
            "statuses": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional iteration statuses to keep in each key result, such as Executing, Completed, Failed, Canceled, or Paused."
            },
            "skip": { "type": "integer" },
            "take": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkIterationKeyTypeQuerySchema = """
        {
          "type": "object",
          "properties": {
            "kind": {
              "type": "string",
              "description": "Optional key kind: Subject, ConcurrencyKey, or Identifier."
            },
            "search": {
              "type": "string",
              "description": "Contains search across key type names. Useful for broad execution requests like claim work or customer work."
            },
            "type": {
              "type": "string",
              "description": "Exact key type to return across key kinds, such as claim, customer, order, or tenant."
            },
            "statuses": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional iteration statuses to keep in each key type result, such as Executing, Completed, Failed, Canceled, or Paused."
            },
            "skip": { "type": "integer" },
            "take": { "type": "integer" }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkerActionSchema = """
        {
          "type": "object",
          "properties": {
            "workerId": {
              "type": "string",
              "description": "Worker id from query_workers or get_worker."
            },
            "revision": {
              "type": "integer",
              "description": "Current worker revision from query_workers or get_worker. Required for optimistic concurrency."
            },
            "description": {
              "type": "string",
              "description": "Optional request context stored on the worker origin so callers can attach human-readable intent or audit notes."
            }
          },
          "required": ["workerId", "revision"],
          "additionalProperties": false
        }
        """;

    private const string WorkflowStartSchema = """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Workflow definition name."
            },
            "waitForCompletion": {
              "type": "boolean",
              "description": "When true, waits for the workflow run to complete before returning."
            },
            "input": {
              "description": "Optional JSON input made available to workflow steps bound to workflow input."
            },
            "description": {
              "type": "string",
              "description": "Optional request context stored on the workflow origin so callers can attach human-readable intent or audit notes."
            }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """;

    private const string WorkflowRunQuerySchema = """
        {
          "type": "object",
          "properties": {
            "definitionName": {
              "type": "string",
              "description": "Optional workflow definition name to filter runs."
            },
            "name": {
              "type": "string",
              "description": "Alias for definitionName."
            },
            "includeFinal": {
              "type": "boolean",
              "description": "When true, includes completed, failed, and canceled runs."
            },
            "childSampleSize": {
              "type": "integer",
              "minimum": 0,
              "maximum": 25,
              "description": "Maximum child workers to include in each compact sample."
            },
            "skip": {
              "type": "integer",
              "minimum": 0,
              "description": "Number of visible workflow runs to skip."
            },
            "take": {
              "type": "integer",
              "minimum": 1,
              "maximum": 100,
              "description": "Maximum visible workflow runs to return."
            }
          },
          "additionalProperties": false
        }
        """;

    private const string WorkflowRunGetSchema = """
        {
          "type": "object",
          "properties": {
            "runId": {
              "type": "string",
              "description": "Workflow run id."
            },
            "childSampleSize": {
              "type": "integer",
              "minimum": 0,
              "maximum": 25,
              "description": "Maximum child workers to include in each compact sample."
            }
          },
          "required": ["runId"],
          "additionalProperties": false
        }
        """;

    private const string WorkflowActionSchema = """
        {
          "type": "object",
          "properties": {
            "runId": {
              "type": "string",
              "description": "Workflow run id returned from workable_start_workflow."
            },
            "description": {
              "type": "string",
              "description": "Optional request context stored on the workflow action origin so callers can attach human-readable intent or audit notes."
            }
          },
          "required": ["runId"],
          "additionalProperties": false
        }
        """;

    private const string WorkDefinitionReconfigurationSchema = """
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "Work definition name from query_work_definitions or get_work_info."
            },
            "revision": {
              "type": "integer",
              "description": "Current work definition revision. Required for optimistic concurrency."
            },
            "defaultOptions": {
              "type": "object",
              "description": "Optional replacement default WorkerOptions for future workers."
            },
            "configuration": {
              "type": "object",
              "description": "Optional replacement default WorkConfiguration for future workers."
            },
            "changes": {
              "type": "object",
              "description": "Optional WorkDefinitionReconfiguration object containing defaultOptions and/or configuration."
            },
            "description": {
              "type": "string",
              "description": "Optional request context stored on the operation origin so callers can attach human-readable intent or audit notes."
            }
          },
          "required": ["name", "revision"],
          "additionalProperties": false
        }
        """;
}
