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

/// <summary>
/// Registers and maps the standard ASP.NET Core MCP server surface for Workable.
/// </summary>
public static class WorkableMcpServerExtensions
{
    /// <summary>
    /// Adds the ASP.NET Core MCP server integration for Workable.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional server configuration for tool exposure and invocation behavior.</param>
    /// <returns>The same service collection for chaining.</returns>
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

    /// <summary>
    /// Maps the Workable MCP endpoint for the default or a named system.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="pattern">The HTTP route pattern to map.</param>
    /// <param name="systemName">The Workable system name to expose, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="authorizationPolicy">
    /// Optional host-defined authorization policy for this mapping. When omitted, the host's default policy applies.
    /// </param>
    /// <param name="useHostFallbackPolicy">
    /// Whether to leave the endpoint without authorization metadata so the host's fallback policy applies.
    /// </param>
    /// <returns>The endpoint convention builder for further endpoint customization.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the selected system does not exist or does not require authorization.</exception>
    public static IEndpointConventionBuilder MapWorkableMcp(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/workable/mcp",
        string? systemName = null,
        string? authorizationPolicy = null,
        bool useHostFallbackPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ValidateHostAuthorizationSelection(authorizationPolicy, useHostFallbackPolicy);
        EnsureSystemRequiresAuthorization(
            endpoints.ServiceProvider.GetRequiredService<IWorkSystemRegistry>(),
            systemName);

        var builder = endpoints.MapMcp(pattern);
        RequireAuthenticated(builder);
        ApplyHostAuthorization(builder, authorizationPolicy, useHostFallbackPolicy);
        builder.WithMetadata(new WorkableMcpEndpointMetadata(systemName));
        return builder;
    }

    private static void ApplyHostAuthorization(
        IEndpointConventionBuilder builder,
        string? authorizationPolicy,
        bool useHostFallbackPolicy)
    {
        if (authorizationPolicy is not null)
        {
            builder.RequireAuthorization(authorizationPolicy);
        }
        else if (!useHostFallbackPolicy)
        {
            builder.RequireAuthorization();
        }
    }

    private static void ValidateHostAuthorizationSelection(
        string? authorizationPolicy,
        bool useHostFallbackPolicy)
    {
        if (authorizationPolicy is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        }

        if (authorizationPolicy is not null && useHostFallbackPolicy)
        {
            throw new ArgumentException(
                "A named authorization policy and the host fallback policy cannot both be selected.",
                nameof(useHostFallbackPolicy));
        }
    }

    private static async ValueTask<ListToolsResult> ListTools(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("MCP request services were not available.");
        var router = services.GetRequiredService<WorkableMcpToolRouter>();
        var options = services.GetRequiredService<IOptions<WorkableMcpServerOptions>>().Value;
        var systemName = GetSystemName(services);
        var requestContext = await GetRequestContext(services, systemName, cancellationToken);
        var tools = (await GetTools(router, requestContext, options, systemName, cancellationToken))
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

        return new ListToolsResult
        {
            Tools = tools,
        };
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
        var systemName = GetSystemName(services);
        var requestContext = await GetRequestContext(services, systemName, cancellationToken);
        var result = await router.CallTool(toolName, arguments, options, systemName, requestContext, cancellationToken);

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

    private static async ValueTask<IReadOnlyList<WorkableMcpServerToolDescriptor>> GetTools(
        WorkableMcpToolRouter router,
        WorkRequestContext requestContext,
        WorkableMcpServerOptions options,
        string? systemName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await router.GetTools(requestContext, options, systemName, cancellationToken);
        }
        catch (WorkSystemAccessDeniedException)
        {
            return [];
        }
    }

    private static async Task<WorkRequestContext> GetRequestContext(
        IServiceProvider services,
        string? systemName,
        CancellationToken cancellationToken)
    {
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        if (httpContext is null)
        {
            throw new InvalidOperationException("Workable MCP requires an HTTP request context.");
        }

        if (!await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
        {
            throw new InvalidOperationException("Workable MCP requires an authenticated user.");
        }

        var requestContext = services.GetRequiredService<IWorkRequestContextFactory>()
            .Create(httpContext, WorkInvocationChannel.Mcp)
            .WithSurface(WorkOriginSurface.WorkableAdapter);
        var resolvedSystemName = ResolveSystem(
            services.GetRequiredService<IWorkSystemRegistry>(),
            systemName).Name;
        var groups = await services.GetRequiredService<IWorkAuthorizationGroupResolver>()
            .GetGroups(requestContext, resolvedSystemName, cancellationToken);
        return requestContext with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                resolvedSystemName,
                requestContext.Actor,
                groups,
                readableDefinitionIds: null,
                isAuthenticated: requestContext.IsAuthenticated),
        };
    }

    private static void EnsureSystemRequiresAuthorization(
        IWorkSystemRegistry registry,
        string? systemName)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var system = ResolveSystem(registry, systemName);
        if (system.RequiresAuthorization)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Workable MCP requires authorization-enabled systems. System '{system.Name ?? "<default>"}' does not require authorization.");
    }

    private static IWorkSystem ResolveSystem(IWorkSystemRegistry registry, string? systemName)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return string.IsNullOrWhiteSpace(systemName)
            ? registry.Default
            : registry.TryGet(systemName, out var namedSystem)
                ? namedSystem
                : throw new InvalidOperationException($"Workable system '{systemName}' was not found.");
    }

    private static void RequireAuthenticated(IEndpointConventionBuilder builder)
    {
        builder.Add(endpointBuilder =>
        {
            var next = endpointBuilder.RequestDelegate!;
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (!await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
                {
                    if (!await WorkableAspNetCoreAuthentication.ChallengeAsync(httpContext))
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    }
                    return;
                }

                await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(httpContext);

                await next(httpContext);
            };
        });
    }

    private sealed record WorkableMcpEndpointMetadata(string? SystemName);
}
