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
            WorkableHttpSystemResolver systems) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            return Results.Ok(WorkableHttpQueueRequestDescriptor.Create(system));
        });

        group.MapPost("/work/{name}", async (
            string name,
            WorkableHttpWorkRequest? request,
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueueAdapter queue,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                $"Queue work '{name}' through HTTP API.");
            var result = await queue.Enqueue(session, name, request, cancellationToken);
            return WorkableHttpRouteResults.ToQueueHttpResult(result);
        });

        group.MapPost("/definitions/{definitionId:guid}/queue", async (
            Guid definitionId,
            WorkableHttpWorkRequest? request,
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueueAdapter queue,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                $"Queue work definition '{definitionId:D}' through HTTP API.");
            var result = await queue.Enqueue(session, new WorkDefinitionId(definitionId), request, cancellationToken);
            return WorkableHttpRouteResults.ToQueueHttpResult(result);
        });
    }
}
