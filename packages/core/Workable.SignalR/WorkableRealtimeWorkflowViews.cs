using System.Text.Json;

namespace Workable;

internal static class WorkableRealtimeWorkflowViews
{
    private static readonly WorkflowRunViewAdapter Views = new();

    public static bool IsWorkflowView(string viewName)
        => string.Equals(viewName, "workflow-runs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(viewName, "workflow-run", StringComparison.OrdinalIgnoreCase);

    public static WorkEventFilter CreateEventFilter(WorkViewCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var definitionNames = (criteria.Components ?? [])
            .Where(request => string.Equals(request.Type, "workflowRuns", StringComparison.OrdinalIgnoreCase))
            .Select(request => GetStringOption(request.Options, "definitionName"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new WorkEventFilter(
            DefinitionNames: definitionNames.Count > 0 ? definitionNames : null)
        {
            DefinitionKind = WorkEventDefinitionKind.Workflow,
        };
    }

    public static async Task<WorkComponentQueryResult> Query(
        IWorkSystem system,
        WorkAuthorizationSnapshot authorization,
        string viewName,
        WorkViewCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentNullException.ThrowIfNull(criteria);

        var requestContext = CreateRequestContext(authorization);
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in criteria.Components ?? [])
        {
            components[request.Id] = await CreateComponent(
                system,
                requestContext,
                viewName,
                request,
                cancellationToken);
        }

        return new WorkComponentQueryResult(DateTimeOffset.UtcNow, components);
    }

    public static WorkRequestContext CreateRequestContext(WorkAuthorizationSnapshot authorization)
        => new(
            WorkOrigin.Create(
                WorkInvocationChannel.SignalR,
                authorization.Actor,
                WorkOriginSurface.WorkableAdapter),
            Authorization: authorization,
            IsAuthenticated: authorization.IsAuthenticated);

    private static async Task<WorkComponentResult> CreateComponent(
        IWorkSystem system,
        WorkRequestContext requestContext,
        string viewName,
        WorkComponentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var componentType = request.Type.Trim().ToLowerInvariant();
            return componentType switch
            {
                "workflowruns" when string.Equals(viewName, "workflow-runs", StringComparison.OrdinalIgnoreCase)
                    => await CreateWorkflowRunsComponent(system, requestContext, request, cancellationToken),
                "workflowrun" when string.Equals(viewName, "workflow-run", StringComparison.OrdinalIgnoreCase)
                    => await CreateWorkflowRunComponent(system, requestContext, request, cancellationToken),
                _ => new WorkComponentResult("error", Error: $"Unknown component '{request.Type}'.", Shape: request.Shape),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return new WorkComponentResult("error", Error: exception.Message, Shape: request.Shape);
        }
        catch (InvalidOperationException exception)
        {
            return new WorkComponentResult("error", Error: exception.Message, Shape: request.Shape);
        }
        catch (JsonException exception)
        {
            return new WorkComponentResult("error", Error: exception.Message, Shape: request.Shape);
        }
    }

    private static async Task<WorkComponentResult> CreateWorkflowRunsComponent(
        IWorkSystem system,
        WorkRequestContext requestContext,
        WorkComponentRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOptions(request.Options, "includeFinal", "definitionName", "childSampleSize", "skip", "take");
        var includeFinal = GetBooleanOption(request.Options, "includeFinal");
        var definitionName = GetStringOption(request.Options, "definitionName");
        var childSampleSize = GetInt32Option(request.Options, "childSampleSize") ?? 3;
        var skip = GetInt32Option(request.Options, "skip") ?? 0;
        var take = GetInt32Option(request.Options, "take") ?? 50;
        if (skip is < 0 or > WorkflowRunViewAdapter.MaximumRunPageSkip ||
            take is < 1 or > WorkflowRunViewAdapter.MaximumRunPageSize)
        {
            throw new ArgumentException(
                $"Workflow run paging requires a non-negative skip no greater than {WorkflowRunViewAdapter.MaximumRunPageSkip} and take between 1 and {WorkflowRunViewAdapter.MaximumRunPageSize}.");
        }

        var result = await Views.RunsPage(
            system,
            requestContext,
            includeFinal,
            definitionName,
            childSampleSize,
            skip,
            take,
            cancellationToken);
        return new WorkComponentResult("ok", result, Shape: request.Shape);
    }

    private static async Task<WorkComponentResult> CreateWorkflowRunComponent(
        IWorkSystem system,
        WorkRequestContext requestContext,
        WorkComponentRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOptions(request.Options, "runId", "childSampleSize");
        var runIdText = GetStringOption(request.Options, "runId");
        if (!Guid.TryParse(runIdText, out var runId))
        {
            return new WorkComponentResult(
                "error",
                Error: "Workflow run views require a valid 'runId' option.",
                Shape: request.Shape);
        }

        var childSampleSize = GetInt32Option(request.Options, "childSampleSize") ?? 3;
        var result = await Views.Run(
            system,
            requestContext,
            new WorkflowRunId(runId),
            childSampleSize,
            cancellationToken);
        return result is null
            ? new WorkComponentResult(
                "error",
                Error: $"Workflow run '{runId:D}' was not found.",
                Shape: request.Shape)
            : new WorkComponentResult("ok", result, Shape: request.Shape);
    }

    private static bool GetBooleanOption(JsonElement? options, string propertyName)
    {
        if (options is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new ArgumentException($"Workflow view option '{propertyName}' must be a boolean.");
    }

    private static int? GetInt32Option(JsonElement? options, string propertyName)
    {
        if (options is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : throw new ArgumentException($"Workflow view option '{propertyName}' must be an integer.");
    }

    private static string? GetStringOption(JsonElement? options, string propertyName)
    {
        if (options is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : throw new ArgumentException($"Workflow view option '{propertyName}' must be a non-empty string.");
    }

    private static void ValidateOptions(JsonElement? options, params string[] allowedProperties)
    {
        if (options is null)
        {
            return;
        }

        if (options.Value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Workflow view options must be a JSON object.");
        }

        var allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        foreach (var property in options.Value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ArgumentException($"Workflow view option '{property.Name}' is not supported.");
            }
        }
    }
}
