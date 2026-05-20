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

    private static void RequireAuthenticated(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(static (context, next) =>
        {
            if (!WorkableAspNetCoreAuthentication.IsAuthenticated(context.HttpContext))
            {
                return ValueTask.FromResult<object?>(WorkableHttpRouteResults.AuthenticationRequired());
            }

            return next(context);
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
