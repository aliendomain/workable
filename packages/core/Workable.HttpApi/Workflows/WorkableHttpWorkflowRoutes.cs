using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpWorkflowRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/workflow-runs", async (
            HttpContext httpContext,
            bool? includeFinal,
            string? definitionName,
            int? childSampleSize,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkflowAdapter workflows,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
                .WithSurface(WorkOriginSurface.WorkableAdapter);
            var result = await workflows.Runs(
                system,
                requestContext,
                includeFinal ?? false,
                definitionName,
                childSampleSize ?? 3,
                cancellationToken);
            return Results.Ok(result);
        });

        group.MapGet("/workflow-runs/{runId:guid}", async (
            HttpContext httpContext,
            Guid runId,
            int? childSampleSize,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkflowAdapter workflows,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
                .WithSurface(WorkOriginSurface.WorkableAdapter);
            var result = await workflows.Run(
                system,
                new WorkflowRunId(runId),
                requestContext,
                childSampleSize ?? 3,
                cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/workflows/{workflowName}", async (
            string workflowName,
            WorkableHttpWorkflowStartRequest? request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkflowAdapter workflows,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
                .WithSurface(WorkOriginSurface.WorkableAdapter);
            var result = await workflows.Start(
                system,
                workflowName,
                string.IsNullOrWhiteSpace(request?.Description)
                    ? requestContext
                    : requestContext with { Description = request.Description },
                request,
                cancellationToken);
            return WorkableHttpRouteResults.ToWorkflowStartHttpResult(result);
        });

        group.MapPost("/workflow-runs/{runId:guid}/actions/{action}", async (
            Guid runId,
            string action,
            WorkableHttpWorkflowActionRequest? request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkflowAdapter workflows,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteBinding.TryParseWorkflowAction(action, out var parsedAction))
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error("workable.http.workflow.action.invalid", $"Workflow action '{action}' is not supported.", "action"),
                    },
                });
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
                .WithSurface(WorkOriginSurface.WorkableAdapter);
            var result = await workflows.Execute(
                system,
                new WorkflowRunId(runId),
                parsedAction,
                string.IsNullOrWhiteSpace(request?.Description)
                    ? requestContext
                    : requestContext with { Description = request.Description },
                cancellationToken);
            return WorkableHttpRouteResults.ToWorkflowActionHttpResult(result);
        });
    }
}
