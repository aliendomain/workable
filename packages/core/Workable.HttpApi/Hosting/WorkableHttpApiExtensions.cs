using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net;

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
    /// <returns>The same endpoint route builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when any registered system does not require authorization.</exception>
    public static IEndpointRouteBuilder MapWorkableApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workable")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        EnsureAllSystemsRequireAuthorization(endpoints.ServiceProvider.GetRequiredService<IWorkSystemRegistry>());

        if (ShouldMapDebugRoutes(endpoints.ServiceProvider))
        {
            var debugGroup = endpoints.MapGroup(prefix);
            RequireOuterGate(debugGroup, endpoints.ServiceProvider);
            WorkableHttpDebugRoutes.Map(debugGroup);
            var namedDebugGroup = endpoints.MapGroup($"{prefix}/systems/{{systemName}}");
            RequireOuterGate(namedDebugGroup, endpoints.ServiceProvider);
            WorkableHttpDebugRoutes.Map(namedDebugGroup);
        }

        var hostGroup = CreateProtectedGroup(
            endpoints,
            prefix,
            requireBuiltInSurfaceAccess: false);
        hostGroup.MapGet("/host", (
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess)
            => Results.Ok(topology.DescribeBuiltInSurfaceHost(requestAccess)));

        var group = CreateProtectedGroup(
            endpoints,
            prefix,
            requireBuiltInSurfaceAccess: true);
        MapWorkableApiRoutes(group);

        var namedGroup = CreateProtectedGroup(
            endpoints,
            $"{prefix}/systems/{{systemName}}",
            requireBuiltInSurfaceAccess: true);
        MapWorkableApiRoutes(namedGroup);

        return endpoints;
    }

    private static RouteGroupBuilder CreateProtectedGroup(
        IEndpointRouteBuilder endpoints,
        string prefix,
        bool requireBuiltInSurfaceAccess)
    {
        var group = endpoints.MapGroup(prefix);
        ApplyTransportAuthorization(group, endpoints.ServiceProvider);
        if (requireBuiltInSurfaceAccess)
        {
            RequireBuiltInSurfaceAccess(group);
        }

        RequireOuterGate(group, endpoints.ServiceProvider);
        RequireAuthenticated(group);
        HandleAuthorizationDenied(group);
        return group;
    }

    private static void MapWorkableApiRoutes(RouteGroupBuilder group)
    {
        WorkableHttpSystemRoutes.Map(group);
        WorkableHttpCatalogRoutes.Map(group);
        WorkableHttpQueueRoutes.Map(group);
        WorkableHttpQueryRoutes.Map(group);
        WorkableHttpWorkflowRoutes.Map(group);
        WorkableHttpWorkerRoutes.Map(group);
        WorkableHttpProfilingRoutes.Map(group);
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
                return WorkableHttpRouteResults.AuthorizationDenied(denied);
            }
        });
    }

    private static void ApplyTransportAuthorization(RouteGroupBuilder group, IServiceProvider services)
    {
        var transportScheme = services
            .GetService<IOptions<WorkableAspNetCoreAuthorizationOptions>>()
            ?.Value
            .TransportAuthenticationScheme;

        if (string.IsNullOrWhiteSpace(transportScheme))
        {
            return;
        }

        group.RequireAuthorization(new AuthorizationPolicyBuilder(transportScheme)
            .RequireAuthenticatedUser()
            .Build());
    }

    private static void RequireAuthenticated(RouteGroupBuilder group)
    {
        ((IEndpointConventionBuilder)group).Add(endpointBuilder =>
        {
            var next = endpointBuilder.RequestDelegate
                ?? throw new InvalidOperationException("Workable HTTP API endpoint did not provide a request delegate.");
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (!HttpMethods.IsOptions(httpContext.Request.Method) &&
                    !await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
                {
                    await WorkableHttpRouteResults.AuthenticationRequired().ExecuteAsync(httpContext);
                    return;
                }

                await next(httpContext);
            };
        });
    }

    private static void RequireOuterGate(RouteGroupBuilder group, IServiceProvider services)
    {
        var requiredGroups = services
            .GetService<IOptions<WorkableHttpApiOptions>>()
            ?.Value
            ?.SurfaceAccessGroups
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
            var next = endpointBuilder.RequestDelegate
                ?? throw new InvalidOperationException("Workable HTTP API endpoint did not provide a request delegate.");
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (HttpMethods.IsOptions(httpContext.Request.Method))
                {
                    await next(httpContext);
                    return;
                }

                var principal = await WorkableAspNetCoreAuthentication.GetAuthenticatedPrincipalAsync(httpContext);
                if (principal?.Identity?.IsAuthenticated != true)
                {
                    await WorkableHttpRouteResults.AuthenticationRequired().ExecuteAsync(httpContext);
                    return;
                }

                var requestAccess = httpContext.RequestServices.GetRequiredService<WorkableHttpRequestAccessContext>();
                if (!requestAccess.HasAnyRequiredSurfaceGroup(requiredGroups))
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
            var next = endpointBuilder.RequestDelegate
                ?? throw new InvalidOperationException("Workable HTTP API endpoint did not provide a request delegate.");
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

                var requestAccess = httpContext.RequestServices.GetRequiredService<WorkableHttpRequestAccessContext>();
                if (!requestAccess.IsBuiltInSurfaceAllowed(system))
                {
                    await WorkableHttpRouteResults.SystemSurfaceAccessDenied(system.Name).ExecuteAsync(httpContext);
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

    private static bool ShouldMapDebugRoutes(IServiceProvider services)
    {
        var environment = services.GetService<IWebHostEnvironment>();
        if (environment?.IsDevelopment() == true)
        {
            return true;
        }

        var configuration = services.GetService<IConfiguration>();
        var configuredUrls = GetConfiguredUrls(configuration).ToArray();
        return configuredUrls.Length > 0 && configuredUrls.All(IsLoopbackUrl);
    }

    private static IEnumerable<string> GetConfiguredUrls(IConfiguration? configuration)
    {
        return new[]
        {
            configuration?["ASPNETCORE_URLS"],
            configuration?["URLS"],
            configuration?["urls"],
        }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}
