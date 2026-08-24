using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Maps the standard Workable HTTP API routes into an ASP.NET Core endpoint builder.
/// </summary>
public static class WorkableHttpApiExtensions
{
    /// <summary>
    /// Maps the default Workable HTTP API route set at the supplied prefix.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="prefix">The route prefix under which Workable HTTP endpoints should be exposed.</param>
    /// <param name="authorizationPolicy">
    /// Optional host-defined authorization policy for this mapping. When omitted, the host's default policy applies.
    /// </param>
    /// <param name="useHostFallbackPolicy">
    /// Whether to leave the endpoints without authorization metadata so the host's fallback policy applies.
    /// </param>
    /// <returns>The same endpoint route builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when any registered system does not require authorization.</exception>
    public static IEndpointRouteBuilder MapWorkableApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workable",
        string? authorizationPolicy = null,
        bool useHostFallbackPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ValidateHostAuthorizationSelection(authorizationPolicy, useHostFallbackPolicy);
        EnsureAllSystemsRequireAuthorization(endpoints.ServiceProvider.GetRequiredService<IWorkSystemRegistry>());

        var hostGroup = CreateProtectedGroup(
            endpoints,
            prefix,
            requireBuiltInSurfaceAccess: false,
            authorizationPolicy: authorizationPolicy,
            useHostFallbackPolicy: useHostFallbackPolicy);
        hostGroup.MapGet("/host", async (
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            CancellationToken cancellationToken)
            => Results.Ok(await topology.DescribeBuiltInSurfaceHost(requestAccess, cancellationToken)));

        var group = CreateProtectedGroup(
            endpoints,
            prefix,
            requireBuiltInSurfaceAccess: true,
            authorizationPolicy: authorizationPolicy,
            useHostFallbackPolicy: useHostFallbackPolicy);
        var executionDiagnosticsAvailable =
            endpoints.ServiceProvider.GetService<IWorkExecutionDiagnosticsRepository>() is not null;
        MapWorkableApiRoutes(group, executionDiagnosticsAvailable);

        var namedGroup = CreateProtectedGroup(
            endpoints,
            $"{prefix}/systems/{{systemName}}",
            requireBuiltInSurfaceAccess: true,
            authorizationPolicy: authorizationPolicy,
            useHostFallbackPolicy: useHostFallbackPolicy);
        MapWorkableApiRoutes(namedGroup, executionDiagnosticsAvailable);

        return endpoints;
    }

    private static RouteGroupBuilder CreateProtectedGroup(
        IEndpointRouteBuilder endpoints,
        string prefix,
        bool requireBuiltInSurfaceAccess,
        string? authorizationPolicy,
        bool useHostFallbackPolicy)
    {
        var group = endpoints.MapGroup(prefix);
        ApplyHostAuthorization(group, authorizationPolicy, useHostFallbackPolicy);
        if (requireBuiltInSurfaceAccess)
        {
            RequireBuiltInSurfaceAccess(group);
        }

        RequireOuterGate(group, endpoints.ServiceProvider);
        RequireAuthenticated(group);
        HandleAuthorizationDenied(group);
        return group;
    }

    private static void ApplyHostAuthorization(
        RouteGroupBuilder group,
        string? authorizationPolicy,
        bool useHostFallbackPolicy)
    {
        if (authorizationPolicy is not null)
        {
            group.RequireAuthorization(authorizationPolicy);
        }
        else if (!useHostFallbackPolicy)
        {
            group.RequireAuthorization();
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

    private static void MapWorkableApiRoutes(
        RouteGroupBuilder group,
        bool executionDiagnosticsAvailable)
    {
        WorkableHttpSystemRoutes.Map(group);
        WorkableHttpCatalogRoutes.Map(group);
        WorkableHttpQueueRoutes.Map(group);
        WorkableHttpQueryRoutes.Map(group);
        WorkableHttpWorkflowRoutes.Map(group);
        WorkableHttpWorkerRoutes.Map(group);
        WorkableHttpProfilingRoutes.Map(group);
        if (executionDiagnosticsAvailable)
        {
            WorkableHttpExecutionDiagnosticsRoutes.Map(group);
        }
    }

    private static void HandleAuthorizationDenied(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (WorkSystemAccessDeniedException denied)
            {
                var systemName = context.HttpContext.Request.RouteValues.TryGetValue("systemName", out var routeValue)
                    ? Convert.ToString(routeValue)
                    : null;
                if (denied.Permission == WorkSystemPermission.AccessSystem &&
                    !string.IsNullOrWhiteSpace(systemName))
                {
                    return WorkableHttpRouteResults.SystemNotFound(systemName);
                }

                return WorkableHttpRouteResults.AuthorizationDenied(denied);
            }
        });
    }

    private static void RequireAuthenticated(RouteGroupBuilder group)
    {
        ((IEndpointConventionBuilder)group).Add(endpointBuilder =>
        {
            var next = endpointBuilder.RequestDelegate!;
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (!HttpMethods.IsOptions(httpContext.Request.Method) &&
                    !await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
                {
                    await WorkableHttpRouteResults.ChallengeAuthentication(httpContext);
                    return;
                }

                if (!HttpMethods.IsOptions(httpContext.Request.Method))
                {
                    await WorkableAspNetCoreAuthentication.PrepareAuthorizationSnapshotAsync(httpContext);
                }

                await next(httpContext);
            };
        });
    }

    private static void RequireOuterGate(RouteGroupBuilder group, IServiceProvider services)
    {
        var requiredGroups = services
            .GetRequiredService<IOptions<WorkableHttpApiOptions>>()
            .Value
            .SurfaceAccessGroups
            ?.Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requiredGroups is not { Count: > 0 })
        {
            return;
        }

        ((IEndpointConventionBuilder)group).Add(endpointBuilder =>
        {
            var next = endpointBuilder.RequestDelegate!;
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (HttpMethods.IsOptions(httpContext.Request.Method))
                {
                    await next(httpContext);
                    return;
                }

                var principal = await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(httpContext);
                if (principal is null)
                {
                    await WorkableHttpRouteResults.ChallengeAuthentication(httpContext);
                    return;
                }

                var requestAccess = httpContext.RequestServices.GetRequiredService<WorkableHttpRequestAccessContext>();
                if (!await requestAccess.HasAnyRequiredSurfaceGroup(
                    requiredGroups,
                    httpContext.RequestAborted))
                {
                    await WorkableHttpRouteResults.SurfaceAccessDenied().ExecuteAsync(httpContext);
                    return;
                }

                await next(httpContext);
            };
        });
    }

    private static void RequireBuiltInSurfaceAccess(RouteGroupBuilder group)
    {
        ((IEndpointConventionBuilder)group).Add(endpointBuilder =>
        {
            var next = endpointBuilder.RequestDelegate!;
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (HttpMethods.IsOptions(httpContext.Request.Method))
                {
                    await next(httpContext);
                    return;
                }

                if (!TryResolveBuiltInSurfaceSystem(httpContext, out var system))
                {
                    await next(httpContext);
                    return;
                }

                var systemName = httpContext.Request.RouteValues.TryGetValue("systemName", out var routeValue)
                    ? Convert.ToString(routeValue)
                    : null;
                var isNamedSystem = !string.IsNullOrWhiteSpace(systemName);
                var requestAccess = httpContext.RequestServices.GetRequiredService<WorkableHttpRequestAccessContext>();
                if (!await requestAccess.IsBuiltInSurfaceAllowed(system, httpContext.RequestAborted))
                {
                    await (isNamedSystem
                        ? WorkableHttpRouteResults.SystemNotFound(systemName)
                        : WorkableHttpRouteResults.SystemSurfaceAccessDenied(system.Name)).ExecuteAsync(httpContext);
                    return;
                }

                if (isNamedSystem &&
                    !await requestAccess.HasAnySystemAccess(system, httpContext.RequestAborted))
                {
                    await WorkableHttpRouteResults.SystemNotFound(systemName).ExecuteAsync(httpContext);
                    return;
                }

                await next(httpContext);
            };
        });
    }

    private static bool TryResolveBuiltInSurfaceSystem(
        HttpContext httpContext,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWorkSystem? system)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var topology = httpContext.RequestServices.GetRequiredService<WorkableHttpTopologyResolver>();
        var systemName = httpContext.Request.RouteValues.TryGetValue("systemName", out var routeValue)
            ? Convert.ToString(routeValue)
            : null;
        return topology.TryResolveSystem(systemName, out system);
    }

    private static void EnsureAllSystemsRequireAuthorization(IWorkSystemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var unsecuredSystems = registry.Systems
            .Where(system => !system.RequiresAuthorization)
            .Select(system => system.Name ?? "<default>")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsecuredSystems.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Workable HTTP API requires authorization-enabled systems. The following systems do not require authorization: {string.Join(", ", unsecuredSystems)}.");
    }

}
