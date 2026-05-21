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
        services.AddWorkableAspNetCoreAuthorization();

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
        EnsureSystemRequiresAuthorization(
            endpoints.ServiceProvider.GetRequiredService<IWorkSystemRegistry>(),
            systemName);

        var builder = endpoints.MapMcp(pattern);
        RequireAuthenticated(builder);
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
        var requestContext = GetRequestContext(services, "List Workable MCP tools.");
        var tools = GetTools(router, requestContext, options, systemName)
            .Select(descriptor =>
            {
                var tool = new Tool
                {
                    Name = descriptor.ToolName,
                    Description = descriptor.Description,
                    InputSchema = JsonSerializer.Deserialize<JsonElement>(descriptor.InputSchemaJson),
                };

                if (!string.IsNullOrWhiteSpace(descriptor.OutputSchemaJson))
                {
                    tool.OutputSchema = JsonSerializer.Deserialize<JsonElement>(descriptor.OutputSchemaJson);
                }

                return tool;
            })
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
        JsonElement? arguments = request.Params?.Arguments is null
            ? null
            : JsonSerializer.SerializeToElement(request.Params.Arguments);
        var toolName = request.Params?.Name ?? string.Empty;
        var requestContext = GetRequestContext(services, $"MCP tool '{toolName}'");
        var result = await router.CallTool(toolName, arguments, options, GetSystemName(services), requestContext, cancellationToken);

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

    private static string? GetSystemName(IServiceProvider services)
        => services.GetService<IHttpContextAccessor>()
            ?.HttpContext
            ?.GetEndpoint()
            ?.Metadata
            .GetMetadata<WorkableMcpEndpointMetadata>()
            ?.SystemName;

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> GetTools(
        WorkableMcpToolRouter router,
        WorkRequestContext requestContext,
        WorkableMcpServerOptions options,
        string? systemName)
    {
        try
        {
            return router.GetTools(requestContext, options, systemName);
        }
        catch (WorkSystemAccessDeniedException)
        {
            return [];
        }
    }

    private static WorkRequestContext GetRequestContext(
        IServiceProvider services,
        string description)
    {
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (httpContext is null)
        {
            throw new InvalidOperationException("Workable MCP requires an HTTP request context.");
        }

        if (!WorkableAspNetCoreAuthentication.IsAuthenticated(httpContext))
        {
            throw new InvalidOperationException("Workable MCP requires an authenticated user.");
        }

        return services.GetRequiredService<IWorkRequestContextFactory>()
            .Create(httpContext, WorkInvocationChannel.Mcp, description);
    }

    private static void EnsureSystemRequiresAuthorization(
        IWorkSystemRegistry registry,
        string? systemName)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var system = string.IsNullOrWhiteSpace(systemName)
            ? registry.Default
            : registry.TryGet(systemName, out var namedSystem)
                ? namedSystem
                : throw new InvalidOperationException($"Workable system '{systemName}' was not found.");
        if (system.RequiresAuthorization)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Workable MCP requires authorization-enabled systems. System '{system.Name ?? "<default>"}' does not require authorization.");
    }

    private static void RequireAuthenticated(IEndpointConventionBuilder builder)
    {
        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder is not RouteEndpointBuilder routeEndpointBuilder)
            {
                return;
            }

            routeEndpointBuilder.FilterFactories.Add(static (context, next) => invocationContext =>
            {
                if (!WorkableAspNetCoreAuthentication.IsAuthenticated(invocationContext.HttpContext))
                {
                    return ValueTask.FromResult<object?>(Results.Unauthorized());
                }

                return next(invocationContext);
            });
        });
    }

    private sealed record WorkableMcpEndpointMetadata(string? SystemName);
}
