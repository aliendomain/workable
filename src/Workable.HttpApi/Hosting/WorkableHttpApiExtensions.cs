using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Workable;

public static class WorkableHttpApiExtensions
{
    public static IEndpointRouteBuilder MapWorkableApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workable")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        EnsureAllSystemsRequireAuthorization(endpoints.ServiceProvider.GetRequiredService<IWorkSystemRegistry>());

        var group = endpoints.MapGroup(prefix);
        RequireAuthenticated(group);
        HandleAuthorizationDenied(group);
        group.MapGet("/systems", (
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            IWorkRequestContextFactory requestContexts)
            => Results.Ok(systems.GetSystems(WorkableHttpRequestContext.Create(
                httpContext,
                requestContexts,
                "Discover Workable systems through HTTP API."))));

        MapWorkableApiRoutes(group);
        var namedGroup = group.MapGroup("/systems/{systemName}");
        RequireAuthenticated(namedGroup);
        MapWorkableApiRoutes(namedGroup);

        return endpoints;
    }

    private static void MapWorkableApiRoutes(RouteGroupBuilder group)
    {
        WorkableHttpSystemRoutes.Map(group);
        WorkableHttpCatalogRoutes.Map(group);
        WorkableHttpQueueRoutes.Map(group);
        WorkableHttpQueryRoutes.Map(group);
        WorkableHttpWorkerRoutes.Map(group);
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
