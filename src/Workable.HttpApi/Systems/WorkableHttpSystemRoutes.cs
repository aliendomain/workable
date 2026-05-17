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
            WorkableHttpSystemResolver systems) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            return Results.Ok(WorkableHttpSystemResolver.Diagnostics(system));
        });

        group.MapPost("/lifecycle/start", async (
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpSystemResolver.Start(system, cancellationToken);
            return Results.Ok(result);
        });

        group.MapPost("/lifecycle/stop", async (
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpSystemResolver.Stop(system, WorkableHttpOrigin.Create(httpContext, "Stop Workable system through HTTP API."), cancellationToken);
            return Results.Ok(result);
        });
    }
}
