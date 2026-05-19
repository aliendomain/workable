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
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpQueueAdapter.QueueCore(system, name, request, WorkableHttpOrigin.Create(httpContext, $"Queue work '{name}' through HTTP API."), cancellationToken);
            return WorkableHttpRouteResults.ToQueueHttpResult(result);
        });

        group.MapPost("/definitions/{definitionId:guid}/queue", async (
            Guid definitionId,
            WorkableHttpWorkRequest? request,
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpQueueAdapter.QueueCore(system, new WorkDefinitionId(definitionId), request, WorkableHttpOrigin.Create(httpContext, $"Queue work definition '{definitionId:D}' through HTTP API."), cancellationToken);
            return WorkableHttpRouteResults.ToQueueHttpResult(result);
        });
    }
}
