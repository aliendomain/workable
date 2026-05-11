using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

public static class WorkableHttpApiExtensions
{
    public static IEndpointRouteBuilder MapWorkableApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workable")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(prefix);
        MapWorkableApiRoutes(group);
        MapWorkableApiRoutes(group.MapGroup("/systems/{systemName}"));

        return endpoints;
    }

    private static void MapWorkableApiRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/definitions", (
            HttpContext httpContext,
            WorkableHttpWorkService work) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            return Results.Ok(WorkableHttpWorkService.GetDefinitions(system));
        });

        group.MapPost("/definitions/query", (
            HttpContext httpContext,
            WorkDefinitionQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkDefinitions(query ?? new WorkDefinitionQuery(), cancellationToken));
        });

        group.MapGet("/definitions/{definitionId:guid}/info", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var info = await system.Query.GetWorkInfo(new WorkDefinitionId(definitionId), cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/work/{name}/info", async (
            HttpContext httpContext,
            string name,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var info = await system.Query.GetWorkInfo(name, cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapPost("/work/{name}", async (
            string name,
            WorkableHttpWorkRequest? request,
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.Queue(system, name, request, WorkableHttpOrigin.Create(httpContext, $"Queue work '{name}' through HTTP API."), cancellationToken);
            return ToQueueHttpResult(result);
        });

        group.MapPost("/definitions/{definitionId:guid}/queue", async (
            Guid definitionId,
            WorkableHttpWorkRequest? request,
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.Queue(system, new WorkDefinitionId(definitionId), request, WorkableHttpOrigin.Create(httpContext, $"Queue work definition '{definitionId:D}' through HTTP API."), cancellationToken);
            return ToQueueHttpResult(result);
        });

        group.MapGet("/workers/{workerId:guid}", async (
            HttpContext httpContext,
            Guid workerId,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var worker = await system.Query.GetWorker(new WorkerId(workerId), cancellationToken);
            return worker is null ? Results.NotFound() : Results.Ok(worker);
        });

        group.MapPost("/workers/query", (
            HttpContext httpContext,
            WorkerQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkers(query ?? new WorkerQuery(), cancellationToken));
        });

        group.MapGet("/workers/status-summary", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetWorkerStatusSummary(null, cancellationToken));
        });

        group.MapPost("/workers/status-summary", (
            HttpContext httpContext,
            WorkerQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetWorkerStatusSummary(query, cancellationToken));
        });

        group.MapPost("/workers/{workerId:guid}/actions/{action}", async (
            Guid workerId,
            string action,
            WorkableHttpWorkerActionRequest request,
            HttpContext httpContext,
            WorkableHttpWorkService work,
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

            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.Execute(system, new WorkerId(workerId), parsedAction, request, WorkableHttpOrigin.Create(httpContext, $"Apply worker action '{parsedAction}' through HTTP API."), cancellationToken);
            return ToActionHttpResult(result);
        });

        group.MapPost("/workers/{workerId:guid}/reconfigure", async (
            Guid workerId,
            WorkableHttpWorkerReconfigurationRequest request,
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.Reconfigure(system, new WorkerId(workerId), request, WorkableHttpOrigin.Create(httpContext, "Reconfigure worker through HTTP API."), cancellationToken);
            return ToActionHttpResult(result);
        });
    }

    private static async Task<IResult> ToOk<T>(Task<T> task)
        => Results.Ok(await task);

    private static bool TryResolveSystem(
        HttpContext httpContext,
        WorkableHttpWorkService work,
        out IWorkSystem system,
        out IResult notFound)
    {
        var systemName = httpContext.Request.RouteValues.TryGetValue("systemName", out var value)
            ? Convert.ToString(value)
            : null;
        if (work.TryGetSystem(systemName, out var resolved))
        {
            system = resolved;
            notFound = Results.NotFound();
            return true;
        }

        system = null!;
        notFound = Results.NotFound(new
        {
            Messages = new[]
            {
                WorkMessage.Error("workable.http.system.not_found", $"Workable system '{systemName}' was not found.", "systemName"),
            },
        });
        return false;
    }

    private static IResult ToQueueHttpResult(WorkableHttpWorkResult result)
        => result.Status == WorkableHttpWorkStatus.Rejected
            ? Results.BadRequest(result)
            : Results.Ok(result);

    private static IResult ToActionHttpResult(WorkActionOutcome result)
        => result.Status switch
        {
            WorkActionStatus.Accepted => Results.Ok(result),
            WorkActionStatus.NotFound => Results.NotFound(result),
            WorkActionStatus.Conflict => Results.Conflict(result),
            _ => Results.BadRequest(result),
        };
}
