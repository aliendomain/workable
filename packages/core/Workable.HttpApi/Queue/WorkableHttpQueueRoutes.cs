using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpQueueRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/queue-request/schema", (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            return Results.Ok(WorkableHttpQueueRequestDescriptor.Create(system));
        });

        group.MapPost("/work/{name}", async (
            string name,
            WorkableHttpWorkRequest? request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueueAdapter queue,
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
                requestContexts,
                request?.Description);
            var result = await queue.Enqueue(system.Name, name, requestContext, request, cancellationToken);
            return WorkableHttpRouteResults.ToQueueHttpResult(result);
        });

    }
}
