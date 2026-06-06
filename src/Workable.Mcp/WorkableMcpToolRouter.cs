using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workable;

public sealed class WorkableMcpToolRouter(IWorkSystemRegistry registry)
{
    private const string WorkToolNamePrefix = "workable_work_";
    private const string WorkToolNameBase = "workable_work";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

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
    private const string StartWorkerTool = "workable_start_worker";
    private const string PauseWorkerTool = "workable_pause_worker";
    private const string CancelWorkerTool = "workable_cancel_worker";
    private const string PushWorkerTool = "workable_push_worker";
    private const string PurgeWorkerTool = "workable_purge_worker";
    private const string ReconfigureWorkDefinitionTool = "workable_reconfigure_work_definition";

    public IReadOnlyList<WorkableMcpServerToolDescriptor> GetTools(
        WorkRequestContext requestContext,
        WorkableMcpServerOptions? options = null,
        string? systemName = null)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        options ??= WorkableMcpServerOptions.Default;
        var tools = new List<WorkableMcpServerToolDescriptor>();
        var system = ResolveSystem(systemName);
        EnsureCanAccessNamedSystem(system, systemName, requestContext);
        var session = system.CreateSession(requestContext);

        if (options.IncludeWorkTools)
        {
            tools.AddRange(CreateWorkTools(session, options.ToolCatalog));
        }

        if (options.IncludeQueryTools)
        {
            tools.AddRange(CreateQueryTools());
        }

        if (options.IncludeActionTools)
        {
            tools.AddRange(CreateActionTools());
        }

        return [.. tools.OrderBy(tool => tool.Kind).ThenBy(tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)];
    }

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

            EnsureCanAccessNamedSystem(system, systemName, requestContext);
            var session = system.CreateSession(requestContext);
            var workTools = options.IncludeWorkTools
                ? CreateWorkTools(session, options.ToolCatalog)
                : [];

            if (TryGetWorkToolName(workTools, toolName, out var workName))
            {
                var invocation = await system.CreateSession(WithDescription(
                        requestContext,
                        ReadWorkToolInvocationDescription(arguments)))
                    .InvokeMcpTool(
                        workName,
                        ReadWorkToolInvocationInput(arguments),
                        options.Invocation,
                        cancellationToken);
                return ToToolResult(invocation, invocation.Status == WorkableMcpInvocationStatus.Rejected);
            }

            if (options.IncludeQueryTools)
            {
                switch (toolName)
                {
                    case QueryWorkersTool:
                        return ToToolResult(await session.Query.Workers(ToWorkerCriteria(arguments), cancellationToken: cancellationToken));
                    case GetWorkerTool:
                    {
                        var workerId = ReadRequiredGuid(arguments, "workerId");
                        var worker = await session.Query.Worker(new WorkerId(workerId), cancellationToken: cancellationToken);
                        return ToToolResult(worker is null
                            ? new { found = false, workerId = workerId.ToString("D") }
                            : new { found = true, worker });
                    }
                    case GetWorkerIterationTool:
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
                    case QueryWorkerIterationsTool:
                        return ToToolResult(await session.Query.WorkerIterations(ToWorkerIterationCriteria(arguments), cancellationToken: cancellationToken));
                    case GetWorkInfoTool:
                    {
                        var name = ReadString(arguments, "name");
                        var info = !string.IsNullOrWhiteSpace(name)
                            ? await session.Query.WorkInfo(name, cancellationToken: cancellationToken)
                            : null;

                        return ToToolResult(info is null
                            ? new { found = false, name }
                            : new { found = true, info });
                    }
                    case QueryWorkDefinitionsTool:
                    {
                        var query = new WorkDefinitionCriteria(
                            Name: ReadString(arguments, "name"),
                            Category: ReadString(arguments, "category"),
                            Search: ReadString(arguments, "search"),
                            IncludeSubcategories: ReadBool(arguments, "includeSubcategories") ?? true);
                        return ToToolResult((await session.Query.WorkDefinitions(query, cancellationToken: cancellationToken)).Definitions);
                    }
                    case QueryWorkerKeysTool:
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
                    case QueryWorkerKeyTypesTool:
                        return ToToolResult(await session.Query.WorkerKeyTypes(
                            new WorkerKeyTypeCriteria(
                                Kind: ReadOptionalEnum<WorkKeyKind>(arguments, "kind"),
                                Search: ReadString(arguments, "search"),
                                Type: ReadString(arguments, "type"),
                                States: ReadStates(arguments),
                                Skip: ReadInt(arguments, "skip") ?? 0,
                                Take: ReadInt(arguments, "take") ?? WorkerKeyCriteria.DefaultTake),
                            cancellationToken: cancellationToken));
                    case QueryWorkIterationKeysTool:
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
                    case QueryWorkIterationKeyTypesTool:
                        return ToToolResult(await session.Query.WorkIterationKeyTypes(
                            new WorkIterationKeyTypeCriteria(
                                Kind: ReadOptionalEnum<WorkKeyKind>(arguments, "kind"),
                                Search: ReadString(arguments, "search"),
                                Type: ReadString(arguments, "type"),
                                Statuses: ReadCompletionStatuses(arguments),
                                Skip: ReadInt(arguments, "skip") ?? 0,
                                Take: ReadInt(arguments, "take") ?? WorkIterationKeyCriteria.DefaultTake),
                            cancellationToken: cancellationToken));
                    case GetWorkerStatusSummaryTool:
                        return ToToolResult(await session.Query.WorkerStatusSummary(ToWorkerCriteria(arguments), cancellationToken: cancellationToken));
                }
            }

            if (options.IncludeActionTools)
            {
                switch (toolName)
                {
                    case StartWorkerTool:
                    case PauseWorkerTool:
                    case CancelWorkerTool:
                    case PushWorkerTool:
                    case PurgeWorkerTool:
                    {
                        var workerId = new WorkerId(ReadRequiredGuid(arguments, "workerId"));
                        var revision = ReadRequiredLong(arguments, "revision");
                        var version = new WorkerVersion(workerId, revision);
                        var action = toolName switch
                        {
                            StartWorkerTool => WorkAction.Start,
                            PauseWorkerTool => WorkAction.Pause,
                            CancelWorkerTool => WorkAction.Cancel,
                            PushWorkerTool => WorkAction.Push,
                            _ => WorkAction.Purge,
                        };
                        var actionSession = system.CreateSession(WithDescription(requestContext, ReadString(arguments, "description")));
                        return ToToolResult(await actionSession.Workers.Execute(version, action, cancellationToken));
                    }
                    case ReconfigureWorkDefinitionTool:
                    {
                        var definitionName = ReadRequiredString(arguments, "name");
                        var revision = ReadRequiredLong(arguments, "revision");
                        var changes = TryGetProperty(arguments, "changes", out var changesProperty) &&
                            changesProperty.ValueKind == JsonValueKind.Object
                                ? changesProperty.Deserialize<WorkDefinitionReconfiguration>(JsonOptions) ?? new WorkDefinitionReconfiguration()
                                : new WorkDefinitionReconfiguration(
                                    DefaultOptions: ReadObject<WorkerOptions>(arguments, "defaultOptions"),
                                    Configuration: ReadObject<WorkConfiguration>(arguments, "configuration"));
                        var reconfigureSession = system.CreateSession(WithDescription(requestContext, ReadString(arguments, "description")));
                        if (!reconfigureSession.Catalog.TryGet(definitionName, out var definition))
                        {
                            return ToToolResult(WorkDefinitionReconfigurationOutcome.NotFound(definitionName));
                        }

                        return ToToolResult(await reconfigureSession.Catalog.Reconfigure(
                            new WorkDefinitionVersion(definition.Id, revision),
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
        catch (ArgumentException invalidArguments)
        {
            return InvalidArguments(invalidArguments.Message);
        }
        catch (JsonException invalidArguments)
        {
            return InvalidArguments(invalidArguments.Message);
        }
    }

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

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateQueryTools()
        =>
        [
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
        ];

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateActionTools()
        =>
        [
            new(
                StartWorkerTool,
                "Start or retry a worker that is queued, paused, or failed. Requires the current worker id and revision from get/query worker to avoid conflicting with another caller.",
                WorkerActionSchema,
                null,
                WorkableMcpServerToolKind.Action),
            new(
                PauseWorkerTool,
                "Request a cooperative pause for a running worker or a recurring worker waiting for its next iteration. Requires the current worker id and revision.",
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
        ];

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
            workName = descriptor.WorkName ?? string.Empty;
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

    private IWorkSystem ResolveSystem(string? systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            return registry.Default;
        }

        return registry.TryGet(systemName, out var system)
            ? system
            : throw new InvalidOperationException($"Workable system '{systemName}' was not found.");
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

        error = ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error("workable.mcp.system_not_found", $"Workable system '{systemName}' was not found.", "systemName"),
            },
        }, isError: true);
        return false;
    }

    private static void EnsureCanAccessNamedSystem(
        IWorkSystem system,
        string? systemName,
        WorkRequestContext requestContext)
    {
        if (string.IsNullOrWhiteSpace(systemName) || system.DescribeAccess(requestContext).HasAnyAccess())
        {
            return;
        }

        throw new WorkSystemAccessDeniedException(
            WorkSystemPermission.AccessSystem,
            system.Id,
            system.Name);
    }

    private static WorkableMcpToolResult ToToolResult(object value, bool isError = false)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return new WorkableMcpToolResult(json, ToJsonObject(json), isError);
    }

    private static WorkableMcpToolResult ToToolResult(WorkActionOutcome outcome)
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
        => ReadGuid(arguments, propertyName) ?? throw new ArgumentException($"Required MCP argument '{propertyName}' is missing or invalid.");

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
            : throw new ArgumentException($"Required MCP argument '{propertyName}' is missing or invalid.");

    private static bool? ReadBool(JsonElement? arguments, string propertyName)
        => TryGetProperty(arguments, propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static T? ReadObject<T>(JsonElement? arguments, string propertyName)
        => TryGetProperty(arguments, propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? property.Deserialize<T>(JsonOptions)
            : default;

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
            : throw new ArgumentException($"Missing required argument '{propertyName}'.");

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
