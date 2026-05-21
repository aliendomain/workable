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
            WorkableHttpSystemResolver systems,
            IWorkRequestContextFactory requestContexts) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                "View Workable system diagnostics through HTTP API.");
            return Results.Ok(WorkableHttpSystemResolver.Diagnostics(system, session));
        });

        group.MapPost("/lifecycle/start", async (
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = WorkableHttpRequestContext.Create(
                httpContext,
                requestContexts,
                "Start Workable system through HTTP API.");
            var result = await WorkableHttpSystemResolver.Start(system, requestContext, cancellationToken);
            return Results.Ok(result);
        });

        group.MapPost("/lifecycle/stop", async (
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = WorkableHttpRequestContext.Create(
                httpContext,
                requestContexts,
                "Stop Workable system through HTTP API.");
            var result = await WorkableHttpSystemResolver.Stop(system, requestContext, cancellationToken);
            return Results.Ok(result);
        });
    }
}
