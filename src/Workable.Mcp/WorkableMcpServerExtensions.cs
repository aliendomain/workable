using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Workable;

public static class WorkableMcpServerExtensions
{
    public static IServiceCollection AddWorkableMcpServer(
        this IServiceCollection services,
        Action<WorkableMcpServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<WorkableMcpServerOptions>();
        }

        services.TryAddSingleton<WorkableMcpToolRouter>();
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithListToolsHandler(ListTools)
            .WithCallToolHandler(CallTool);

        return services;
    }

    public static IEndpointConventionBuilder MapWorkableMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/workable/mcp",
        string? systemName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var builder = endpoints.MapMcp(pattern);
        builder.WithMetadata(new WorkableMcpEndpointMetadata(systemName));
        return builder;
    }

    private static ValueTask<ListToolsResult> ListTools(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("MCP request services were not available.");
        var router = services.GetRequiredService<WorkableMcpToolRouter>();
        var options = services.GetRequiredService<IOptions<WorkableMcpServerOptions>>().Value;
        var systemName = GetSystemName(services);
        var tools = router.GetTools(options, systemName)
            .Select(ToProtocolTool)
            .ToList();

        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = tools,
        });
    }

    private static async ValueTask<CallToolResult> CallTool(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("MCP request services were not available.");
        var router = services.GetRequiredService<WorkableMcpToolRouter>();
        var options = services.GetRequiredService<IOptions<WorkableMcpServerOptions>>().Value;
        var arguments = ToJsonElement(request.Params?.Arguments);
        var toolName = request.Params?.Name ?? string.Empty;
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var origin = WorkableMcpOrigin.Create(httpContext, $"MCP tool '{toolName}'");
        var result = await router.CallTool(toolName, arguments, options, GetSystemName(services), origin, cancellationToken);

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = result.Json,
                },
            ],
            StructuredContent = result.StructuredContent,
            IsError = result.IsError,
        };
    }

    private static Tool ToProtocolTool(WorkableMcpServerToolDescriptor descriptor)
    {
        var tool = new Tool
        {
            Name = descriptor.ToolName,
            Description = descriptor.Description,
            InputSchema = ParseJsonObject(descriptor.InputSchemaJson),
        };

        if (!string.IsNullOrWhiteSpace(descriptor.OutputSchemaJson))
        {
            tool.OutputSchema = ParseJsonObject(descriptor.OutputSchemaJson);
        }

        return tool;
    }

    private static JsonElement ParseJsonObject(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    private static JsonElement? ToJsonElement(object? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(arguments);
    }

    private static string? GetSystemName(IServiceProvider services)
        => services.GetService<IHttpContextAccessor>()
            ?.HttpContext
            ?.GetEndpoint()
            ?.Metadata
            .GetMetadata<WorkableMcpEndpointMetadata>()
            ?.SystemName;

    private sealed record WorkableMcpEndpointMetadata(string? SystemName);
}
