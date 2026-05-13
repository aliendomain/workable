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
        group.MapGet("/systems", (WorkableHttpWorkService work)
            => Results.Ok(work.GetSystems()));

        MapWorkableApiRoutes(group);
        MapWorkableApiRoutes(group.MapGroup("/systems/{systemName}"));

        return endpoints;
    }

    private static void MapWorkableApiRoutes(RouteGroupBuilder group)
    {
        group.MapPost("/lifecycle/start", async (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.Start(system, cancellationToken);
            return Results.Ok(result);
        });

        group.MapPost("/lifecycle/stop", async (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.Stop(system, WorkableHttpOrigin.Create(httpContext, "Stop Workable system through HTTP API."), cancellationToken);
            return Results.Ok(result);
        });

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

        group.MapGet("/overview", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverview(cancellationToken));
        });

        group.MapGet("/overview/counts", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewCounts(cancellationToken));
        });

        group.MapGet("/overview/worker-counts", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewWorkerCounts(cancellationToken));
        });

        group.MapGet("/overview/iteration-counts", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewIterationCounts(cancellationToken));
        });

        group.MapGet("/overview/common-key-types", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewCommonKeyTypes(cancellationToken));
        });

        group.MapGet("/overview/failed-workers", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewFailedWorkers(cancellationToken));
        });

        group.MapGet("/overview/failed-iterations", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewFailedIterations(cancellationToken));
        });

        group.MapGet("/overview/completed-iterations", (
            HttpContext httpContext,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetSystemOverviewCompletedIterations(cancellationToken));
        });

        group.MapGet("/queue-request/schema", ()
            => Results.Ok(WorkableHttpQueueRequestDescriptor.Create()));

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

        group.MapPost("/definitions/{definitionId:guid}/reconfigure", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpDefinitionReconfigurationRequest request,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await WorkableHttpWorkService.ReconfigureDefinition(
                system,
                new WorkDefinitionId(definitionId),
                request,
                cancellationToken);
            return ToDefinitionReconfigurationHttpResult(result);
        });

        group.MapGet("/work/id/{definitionId:guid}/info", async (
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

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return notFound;
            }

            var iteration = await system.Query.GetWorkerIteration(new WorkerIterationReference(new WorkerId(workerId), sequence), cancellationToken);
            return iteration is null ? Results.NotFound() : Results.Ok(iteration);
        });

        group.MapPost("/workers/query", (
            HttpContext httpContext,
            WorkableHttpWorkerQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkers(query?.ToWorkerQuery() ?? new WorkerQuery(), cancellationToken));
        });

        group.MapPost("/iterations/query", (
            HttpContext httpContext,
            WorkableHttpWorkerIterationQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkerIterations(query?.ToWorkerIterationQuery() ?? new WorkerIterationQuery(), cancellationToken));
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
            WorkableHttpWorkerQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.GetWorkerStatusSummary(query?.ToWorkerQuery(), cancellationToken));
        });

        group.MapPost("/work-keys/query", (
            HttpContext httpContext,
            WorkerKeyQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkerKeys(query ?? new WorkerKeyQuery(), cancellationToken));
        });

        group.MapGet("/work-keys/types", (
            HttpContext httpContext,
            WorkKeyKind? kind,
            string? type,
            string? search,
            int? skip,
            int? take,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkerKeyTypes(
                new WorkerKeyTypeQuery(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkerKeyQuery.DefaultTake),
                cancellationToken));
        });

        group.MapPost("/work-keys/types/query", (
            HttpContext httpContext,
            WorkableHttpWorkerKeyTypeQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkerKeyTypes(query?.ToWorkerKeyTypeQuery(), cancellationToken));
        });

        group.MapPost("/work-iteration-keys/query", (
            HttpContext httpContext,
            WorkIterationKeyQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkIterationKeys(query ?? new WorkIterationKeyQuery(), cancellationToken));
        });

        group.MapGet("/work-iteration-keys/types", (
            HttpContext httpContext,
            WorkKeyKind? kind,
            string? type,
            string? search,
            int? skip,
            int? take,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkIterationKeyTypes(
                new WorkIterationKeyTypeQuery(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkIterationKeyQuery.DefaultTake),
                cancellationToken));
        });

        group.MapPost("/work-iteration-keys/types/query", (
            HttpContext httpContext,
            WorkableHttpWorkIterationKeyTypeQuery? query,
            WorkableHttpWorkService work,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolveSystem(httpContext, work, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return ToOk(system.Query.QueryWorkIterationKeyTypes(query?.ToWorkIterationKeyTypeQuery(), cancellationToken));
        });

        group.MapPost("/workers/actions/{action}", async (
            string action,
            WorkableHttpWorkerBulkActionRequest? request,
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

            var result = await WorkableHttpWorkService.ExecuteAll(system, parsedAction, request, WorkableHttpOrigin.Create(httpContext, $"Apply worker action '{parsedAction}' to multiple workers through HTTP API."), cancellationToken);
            return Results.Ok(result);
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

    private static IResult ToDefinitionReconfigurationHttpResult(WorkDefinitionReconfigurationOutcome result)
        => result.Status switch
        {
            WorkDefinitionReconfigurationStatus.Accepted => Results.Ok(result),
            WorkDefinitionReconfigurationStatus.NotFound => Results.NotFound(result),
            WorkDefinitionReconfigurationStatus.Conflict => Results.Conflict(result),
            _ => Results.BadRequest(result),
        };
}
