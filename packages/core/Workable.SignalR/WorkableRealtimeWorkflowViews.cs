using System.Text.Json;

namespace Workable;

internal static class WorkableRealtimeWorkflowViews
{
    private static readonly WorkflowRunViewAdapter Views = new();

    public static bool IsWorkflowView(string viewName)
        => string.Equals(viewName, "workflow-runs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(viewName, "workflow-run", StringComparison.OrdinalIgnoreCase);

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
        var includeFinal = GetBooleanOption(request.Options, "includeFinal");
        var definitionName = GetStringOption(request.Options, "definitionName");
        var childSampleSize = GetInt32Option(request.Options, "childSampleSize") ?? 3;
        var result = await Views.Runs(
            system,
            requestContext,
            includeFinal,
            definitionName,
            childSampleSize,
            cancellationToken);
        return new WorkComponentResult("ok", result, Shape: request.Shape);
    }

    private static async Task<WorkComponentResult> CreateWorkflowRunComponent(
        IWorkSystem system,
        WorkRequestContext requestContext,
        WorkComponentRequest request,
        CancellationToken cancellationToken)
    {
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
        => options is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            property.GetBoolean();

    private static int? GetInt32Option(JsonElement? options, string propertyName)
        => options is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
                ? value
                : null;

    private static string? GetStringOption(JsonElement? options, string propertyName)
        => options is { ValueKind: JsonValueKind.Object } element &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
}
