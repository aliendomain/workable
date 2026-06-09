using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpSystemRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/diagnostics", (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts);
            return Results.Ok(WorkableHttpTopologyResolver.Diagnostics(system, session));
        });

        group.MapPost("/lifecycle/start", async (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = WorkableHttpRequestContext.Create(
                httpContext,
                system,
                requestContexts);
            var result = await WorkableHttpTopologyResolver.Start(system, requestContext, cancellationToken);
            return Results.Ok(result);
        });

        group.MapPost("/lifecycle/stop", async (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = WorkableHttpRequestContext.Create(
                httpContext,
                system,
                requestContexts);
            var result = await WorkableHttpTopologyResolver.Stop(system, requestContext, cancellationToken);
            return Results.Ok(result);
        });
    }
}
