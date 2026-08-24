using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Text.Json;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Workable;

internal static class WorkableHttpWorkerRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/workers/actions/{action}", async (
            string action,
            WorkableHttpWorkerBulkActionRequest? request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkerAdapter workers,
            IWorkRequestContextFactory requestContexts,
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

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                request?.Description);
            var result = await workers.ExecuteAll(session, parsedAction, request, cancellationToken);
            return Results.Ok(result);
        });

        group.MapPost("/workers/{workerId:guid}/actions/{action}", async (
            Guid workerId,
            string action,
            WorkableHttpWorkerActionRequest request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkerAdapter workers,
            IWorkRequestContextFactory requestContexts,
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

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts);
            var result = await workers.Execute(session, new WorkerId(workerId), parsedAction, request, cancellationToken);
            return WorkableHttpRouteResults.ToActionHttpResult(result);
        });

        group.MapPost("/workers/{workerId:guid}/reconfigure", async (
            Guid workerId,
            JsonElement requestBody,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkerAdapter workers,
            IWorkRequestContextFactory requestContexts,
            IOptions<HttpJsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            WorkableHttpWorkerReconfigurationRequest request;
            try
            {
                request = WorkableHttpReconfigurationJson.ParseWorker(
                    requestBody,
                    jsonOptions.Value.SerializerOptions);
            }
            catch (WorkableHttpReconfigurationValidationException exception)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error("workable.http.reconfiguration.invalid", exception.Message, "request"),
                    },
                });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.http.reconfiguration.invalid",
                            "The worker reconfiguration request contains invalid JSON values.",
                            "request"),
                    },
                });
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                request.Description);
            var result = await workers.Reconfigure(session, new WorkerId(workerId), request, cancellationToken);
            return WorkableHttpRouteResults.ToActionHttpResult(result);
        });
    }
}
