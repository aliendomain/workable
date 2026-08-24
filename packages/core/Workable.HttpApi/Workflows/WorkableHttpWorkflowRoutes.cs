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
            int? skip,
            int? take,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkflowAdapter workflows,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidChildSampleSize(childSampleSize) || !IsValidRunPage(skip, take))
            {
                return !IsValidChildSampleSize(childSampleSize)
                    ? InvalidChildSampleSize()
                    : InvalidRunPage();
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
                .WithSurface(WorkOriginSurface.WorkableAdapter);
            var result = await workflows.RunsPage(
                system,
                requestContext,
                includeFinal ?? false,
                definitionName,
                childSampleSize ?? 3,
                skip ?? 0,
                take ?? 50,
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
            if (!IsValidChildSampleSize(childSampleSize))
            {
                return InvalidChildSampleSize();
            }

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

        group.MapGet("/workflow-runs/{runId:guid}/steps/{stepName}/children", async (
            HttpContext httpContext,
            Guid runId,
            string stepName,
            int? skip,
            int? take,
            WorkableHttpTopologyResolver topology,
            WorkableHttpWorkflowAdapter workflows,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidChildPage(skip, take))
            {
                return InvalidChildPage();
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var requestContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
                .WithSurface(WorkOriginSurface.WorkableAdapter);
            var result = await workflows.StepChildren(
                system,
                new WorkflowRunId(runId),
                stepName,
                requestContext,
                skip ?? 0,
                take ?? 25,
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

    private static bool IsValidChildSampleSize(int? childSampleSize)
        => childSampleSize is null or >= 0 and <= WorkflowRunViewAdapter.MaximumChildSampleSize;

    private static bool IsValidRunPage(int? skip, int? take)
        => skip is null or >= 0 and <= WorkflowRunViewAdapter.MaximumRunPageSkip &&
            take is null or >= 1 and <= WorkflowRunViewAdapter.MaximumRunPageSize;

    private static bool IsValidChildPage(int? skip, int? take)
        => skip is null or >= 0 and <= WorkflowRunViewAdapter.MaximumChildPageSkip &&
            take is null or >= 1 and <= WorkflowRunViewAdapter.MaximumChildPageSize;

    private static IResult InvalidChildSampleSize()
        => Results.BadRequest(new
        {
            Messages = new[]
            {
                WorkMessage.Error(
                    "workable.http.workflow.child-sample-size.invalid",
                    $"Child sample size must be between 0 and {WorkflowRunViewAdapter.MaximumChildSampleSize}.",
                    "childSampleSize"),
            },
        });

    private static IResult InvalidRunPage()
        => Results.BadRequest(new
        {
            Messages = new[]
            {
                WorkMessage.Error(
                    "workable.http.workflow.run-page.invalid",
                    $"Workflow run paging requires skip between 0 and {WorkflowRunViewAdapter.MaximumRunPageSkip} and take between 1 and {WorkflowRunViewAdapter.MaximumRunPageSize}.",
                    "paging"),
            },
        });

    private static IResult InvalidChildPage()
        => Results.BadRequest(new
        {
            Messages = new[]
            {
                WorkMessage.Error(
                    "workable.http.workflow.child-page.invalid",
                    $"Workflow child paging requires skip between 0 and {WorkflowRunViewAdapter.MaximumChildPageSkip} and take between 1 and {WorkflowRunViewAdapter.MaximumChildPageSize}.",
                    "paging"),
            },
        });
}
