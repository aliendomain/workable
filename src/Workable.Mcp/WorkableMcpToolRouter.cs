using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

public sealed class WorkableMcpToolRouter(IWorkSystemRegistry registry)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string QueryWorkersTool = "workable_query_workers";
    private const string GetWorkerTool = "workable_get_worker";
    private const string GetWorkInfoTool = "workable_get_work_info";
    private const string QueryWorkDefinitionsTool = "workable_query_work_definitions";
    private const string GetWorkerStatusSummaryTool = "workable_get_worker_status_summary";
    private const string StartWorkerTool = "workable_start_worker";
    private const string PauseWorkerTool = "workable_pause_worker";
    private const string CancelWorkerTool = "workable_cancel_worker";
    private const string PushWorkerTool = "workable_push_worker";
    private const string PurgeWorkerTool = "workable_purge_worker";

    public IReadOnlyList<WorkableMcpServerToolDescriptor> GetTools(
        WorkableMcpServerOptions? options = null,
        string? systemName = null)
    {
        options ??= WorkableMcpServerOptions.Default;

        var tools = new List<WorkableMcpServerToolDescriptor>();
        var system = ResolveSystem(systemName);

        if (options.IncludeWorkTools)
        {
            tools.AddRange(CreateWorkTools(system, options.ToolCatalog));
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
        WorkableMcpServerOptions? options = null,
        string? systemName = null,
        CancellationToken cancellationToken = default)
        => await this.CallTool(toolName, arguments, options, systemName, origin: null, cancellationToken);

    internal async Task<WorkableMcpToolResult> CallTool(
        string toolName,
        JsonElement? arguments,
        WorkableMcpServerOptions? options,
        string? systemName,
        WorkOrigin? origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        options ??= WorkableMcpServerOptions.Default;
        if (!TryResolveSystem(systemName, out var system, out var systemError))
        {
            return systemError;
        }

        if (options.IncludeWorkTools &&
            TryGetWorkToolName(system, toolName, options.ToolCatalog, out var workName))
        {
            var invocation = origin is null
                ? await system.InvokeMcpTool(workName, arguments, options.Invocation, cancellationToken)
                : await system.InvokeMcpTool(workName, arguments, options.Invocation, origin, cancellationToken);
            return ToToolResult(invocation, invocation.Status == WorkableMcpInvocationStatus.Rejected);
        }

        if (options.IncludeQueryTools)
        {
            var queryResult = toolName switch
            {
                QueryWorkersTool => ToToolResult(await QueryWorkers(system, arguments, cancellationToken)),
                GetWorkerTool => ToToolResult(await GetWorker(system, arguments, cancellationToken)),
                GetWorkInfoTool => ToToolResult(await GetWorkInfo(system, arguments, cancellationToken)),
                QueryWorkDefinitionsTool => ToToolResult(await QueryWorkDefinitions(system, arguments, cancellationToken)),
                GetWorkerStatusSummaryTool => ToToolResult(await GetWorkerStatusSummary(system, arguments, cancellationToken)),
                _ => null,
            };

            if (queryResult is not null)
            {
                return queryResult;
            }
        }

        if (options.IncludeActionTools)
        {
            var actionResult = toolName switch
            {
                StartWorkerTool => ToToolResult(await ExecuteWorkerAction(system, arguments, WorkAction.Start, origin, cancellationToken)),
                PauseWorkerTool => ToToolResult(await ExecuteWorkerAction(system, arguments, WorkAction.Pause, origin, cancellationToken)),
                CancelWorkerTool => ToToolResult(await ExecuteWorkerAction(system, arguments, WorkAction.Cancel, origin, cancellationToken)),
                PushWorkerTool => ToToolResult(await ExecuteWorkerAction(system, arguments, WorkAction.Push, origin, cancellationToken)),
                PurgeWorkerTool => ToToolResult(await ExecuteWorkerAction(system, arguments, WorkAction.Purge, origin, cancellationToken)),
                _ => null,
            };

            if (actionResult is not null)
            {
                return actionResult;
            }
        }

        return UnknownTool(toolName);
    }

    public static string CreateWorkToolName(string workName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workName);

        var builder = new StringBuilder("workable_work_");
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
        return LimitToolName(toolName.Length == "workable_work".Length ? "workable_work" : toolName);
    }

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateWorkTools(
        IWorkSystem system,
        WorkableMcpToolCatalogOptions catalogOptions)
    {
        var descriptors = system.GetMcpToolDescriptors(catalogOptions);
        var baseNameCounts = descriptors
            .GroupBy(descriptor => CreateWorkToolName(descriptor.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return [.. descriptors.Select(descriptor =>
        {
            var baseName = CreateWorkToolName(descriptor.Name);
            var toolName = baseNameCounts[baseName] == 1
                ? baseName
                : LimitToolName(baseName, descriptor.DefinitionId.Value.ToString("N")[..8]);

            return new WorkableMcpServerToolDescriptor(
                toolName,
                CreateWorkToolDescription(descriptor),
                descriptor.InputSchemaJson,
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
                "Find workers by work name, state, subject, concurrency key, identifier, time range, and paging. Use this before worker actions when you need the current worker id and revision.",
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
                GetWorkInfoTool,
                "Get one work definition plus worker rollup counts by work name or definition id. Use this to understand whether a kind of work is healthy or active.",
                """{"type":"object","properties":{"name":{"type":"string"},"definitionId":{"type":"string"}},"additionalProperties":false}""",
                null,
                WorkableMcpServerToolKind.Query),
            new(
                QueryWorkDefinitionsTool,
                "Browse available work definitions by name, category, search text, or definition id. Use this to discover what work can be queued.",
                WorkDefinitionQuerySchema,
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
        ];

    private static async Task<object> QueryWorkers(
        IWorkSystem system,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var query = ToWorkerQuery(arguments);
        return await system.Query.QueryWorkers(query, cancellationToken);
    }

    private static async Task<object> GetWorker(
        IWorkSystem system,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var workerId = ReadRequiredGuid(arguments, "workerId");
        var worker = await system.Query.GetWorker(new WorkerId(workerId), cancellationToken);
        return worker is null
            ? new { found = false, workerId = workerId.ToString("D") }
            : new { found = true, worker };
    }

    private static async Task<object> GetWorkInfo(
        IWorkSystem system,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var name = ReadString(arguments, "name");
        var definitionId = ReadGuid(arguments, "definitionId");
        WorkInfo? info = null;

        if (definitionId.HasValue)
        {
            info = await system.Query.GetWorkInfo(new WorkDefinitionId(definitionId.Value), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            info = await system.Query.GetWorkInfo(name, cancellationToken);
        }

        return info is null
            ? new { found = false, name, definitionId = definitionId?.ToString("D") }
            : new { found = true, info };
    }

    private static async Task<object> QueryWorkDefinitions(
        IWorkSystem system,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var query = new WorkDefinitionQuery(
            Id: ReadGuid(arguments, "definitionId") is { } id ? new WorkDefinitionId(id) : null,
            Name: ReadString(arguments, "name"),
            Category: ReadString(arguments, "category"),
            Search: ReadString(arguments, "search"),
            IncludeSubcategories: ReadBool(arguments, "includeSubcategories") ?? true);

        return await system.Query.QueryWorkDefinitions(query, cancellationToken);
    }

    private static async Task<object> GetWorkerStatusSummary(
        IWorkSystem system,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var query = ToWorkerQuery(arguments);
        return await system.Query.GetWorkerStatusSummary(query, cancellationToken);
    }

    private static async Task<object> ExecuteWorkerAction(
        IWorkSystem system,
        JsonElement? arguments,
        WorkAction action,
        WorkOrigin? origin,
        CancellationToken cancellationToken)
    {
        var workerId = new WorkerId(ReadRequiredGuid(arguments, "workerId"));
        var revision = ReadRequiredLong(arguments, "revision");
        var version = new WorkerVersion(workerId, revision);

        return origin is null
            ? await system.Workers.Execute(version, action, cancellationToken)
            : await RequiredOriginAwareSystem(system).Execute(version, action, origin, cancellationToken);
    }

    private static WorkerQuery ToWorkerQuery(JsonElement? arguments)
    {
        return new WorkerQuery(
            DefinitionId: ReadGuid(arguments, "definitionId") is { } definitionId ? new WorkDefinitionId(definitionId) : null,
            DefinitionName: ReadString(arguments, "definitionName") ?? ReadString(arguments, "workName"),
            SubjectId: ReadPair(arguments, "subjectType", "subjectValue") is { } subject ? new WorkSubjectId(subject.Type, subject.Value) : null,
            ConcurrencyKey: ReadPair(arguments, "concurrencyKeyType", "concurrencyKeyValue") is { } key ? new WorkConcurrencyKey(key.Type, key.Value) : null,
            Identifier: ReadPair(arguments, "identifierType", "identifierValue") is { } identifier ? new WorkIdentifier(identifier.Type, identifier.Value) : null,
            States: ReadStates(arguments),
            CreatedFrom: ReadDateTimeOffset(arguments, "createdFrom"),
            CreatedTo: ReadDateTimeOffset(arguments, "createdTo"),
            UpdatedFrom: ReadDateTimeOffset(arguments, "updatedFrom"),
            UpdatedTo: ReadDateTimeOffset(arguments, "updatedTo"),
            Sort: ReadEnum(arguments, "sort", WorkerQuerySort.CreatedAt),
            Direction: ReadEnum(arguments, "direction", WorkQuerySortDirection.Descending),
            Skip: ReadInt(arguments, "skip") ?? 0,
            Take: ReadInt(arguments, "take") ?? 100);
    }

    private static bool TryGetWorkToolName(
        IWorkSystem system,
        string toolName,
        WorkableMcpToolCatalogOptions catalogOptions,
        out string workName)
    {
        var descriptor = CreateWorkTools(system, catalogOptions)
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

    private static IOriginAwareWorkSystem RequiredOriginAwareSystem(IWorkSystem system)
        => system as IOriginAwareWorkSystem
            ?? throw new InvalidOperationException("The configured Workable system does not support trusted origin-aware operations.");

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
        out WorkableMcpToolResult error)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            system = registry.Default;
            error = null!;
            return true;
        }

        if (registry.TryGet(systemName, out system))
        {
            error = null!;
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

    private static WorkableMcpToolResult ToToolResult(object value, bool isError = false)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return new WorkableMcpToolResult(json, ToJsonObject(json), isError);
    }

    private static WorkableMcpToolResult UnknownTool(string toolName)
        => ToToolResult(new
        {
            status = "rejected",
            messages = new[]
            {
                WorkMessage.Error("workable.mcp.tool_not_found", $"MCP tool '{toolName}' was not found.", "toolName"),
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

    private static JsonElement? ToJsonObject(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static IReadOnlySet<WorkerState>? ReadStates(JsonElement? arguments)
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

    private static WorkerState? TryParseWorkerState(string? value)
        => Enum.TryParse<WorkerState>(value, ignoreCase: true, out var workerState)
            ? workerState
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

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement? arguments, string propertyName)
        => DateTimeOffset.TryParse(ReadString(arguments, propertyName), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;

    private static TEnum ReadEnum<TEnum>(JsonElement? arguments, string propertyName, TEnum defaultValue)
        where TEnum : struct
        => Enum.TryParse<TEnum>(ReadString(arguments, propertyName), ignoreCase: true, out var value) ? value : defaultValue;

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

    private const string WorkerQuerySchema = """
        {
          "type": "object",
          "properties": {
            "definitionId": { "type": "string" },
            "definitionName": { "type": "string" },
            "workName": { "type": "string" },
            "subjectType": { "type": "string" },
            "subjectValue": { "type": "string" },
            "concurrencyKeyType": { "type": "string" },
            "concurrencyKeyValue": { "type": "string" },
            "identifierType": { "type": "string" },
            "identifierValue": { "type": "string" },
            "states": { "type": "array", "items": { "type": "string" } },
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

    private const string WorkDefinitionQuerySchema = """
        {
          "type": "object",
          "properties": {
            "definitionId": { "type": "string" },
            "name": { "type": "string" },
            "category": { "type": "string" },
            "search": { "type": "string" },
            "includeSubcategories": { "type": "boolean" }
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
            }
          },
          "required": ["workerId", "revision"],
          "additionalProperties": false
        }
        """;
}
