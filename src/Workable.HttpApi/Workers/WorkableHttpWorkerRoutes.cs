using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpWorkerRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/workers/actions/{action}", async (
            string action,
            WorkableHttpWorkerBulkActionRequest? request,
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteBinding.TryParseAction(action, out var parsedAction))
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error("workable.http.action.invalid", $"Worker action '{action}' is not supported.", "action"),
                    },
                });
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkerAdapter.ExecuteAllCore(system, parsedAction, request, WorkableHttpOrigin.Create(httpContext, $"Apply worker action '{parsedAction}' to multiple workers through HTTP API."), cancellationToken);
            return Results.Ok(result);
        });

        group.MapPost("/workers/{workerId:guid}/actions/{action}", async (
            Guid workerId,
            string action,
            WorkableHttpWorkerActionRequest request,
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteBinding.TryParseAction(action, out var parsedAction))
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error("workable.http.action.invalid", $"Worker action '{action}' is not supported.", "action"),
                    },
                });
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkerAdapter.ExecuteCore(system, new WorkerId(workerId), parsedAction, request, WorkableHttpOrigin.Create(httpContext, $"Apply worker action '{parsedAction}' through HTTP API."), cancellationToken);
            return WorkableHttpRouteResults.ToActionHttpResult(result);
        });

        group.MapPost("/workers/{workerId:guid}/reconfigure", async (
            Guid workerId,
            WorkableHttpWorkerReconfigurationRequest request,
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkerAdapter.ReconfigureCore(system, new WorkerId(workerId), request, WorkableHttpOrigin.Create(httpContext, "Reconfigure worker through HTTP API."), cancellationToken);
            return WorkableHttpRouteResults.ToActionHttpResult(result);
        });
    }
}
