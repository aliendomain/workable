using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpQueryRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/components/query", (
            HttpContext httpContext,
            WorkComponentCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.Components(system, query, cancellationToken: cancellationToken));
        });

        group.MapPost("/components/{componentName}", (
            HttpContext httpContext,
            string componentName,
            WorkSingleComponentCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.Components(system, new WorkComponentCriteria(
                    query?.Scope,
                    [new WorkComponentRequest(
                        componentName,
                        componentName,
                        query?.Options,
                        query?.Shape ?? WorkComponentShapes.Detailed)]), cancellationToken: cancellationToken));
        });

        group.MapPost("/views/{viewName}", (
            HttpContext httpContext,
            string viewName,
            WorkViewCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.View(system, viewName, query, cancellationToken: cancellationToken));
        });

        group.MapPost("/definitions/query", async (
            HttpContext httpContext,
            WorkDefinitionCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var definitions = await queries.WorkDefinitions(system, query, cancellationToken: cancellationToken);
            return Results.Ok(definitions);
        });

        group.MapGet("/definitions/{definitionId:guid}/info", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var info = await queries.WorkInfo(system, new WorkDefinitionId(definitionId), cancellationToken: cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/work/id/{definitionId:guid}/info", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var info = await queries.WorkInfo(system, new WorkDefinitionId(definitionId), cancellationToken: cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/work/{name}/info", async (
            HttpContext httpContext,
            string name,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var info = await queries.WorkInfo(system, name, cancellationToken: cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/workers/{workerId:guid}", async (
            HttpContext httpContext,
            Guid workerId,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var worker = await queries.Worker(system, new WorkerId(workerId), cancellationToken: cancellationToken);
            return worker is null ? Results.NotFound() : Results.Ok(worker);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var iteration = await queries.WorkerIteration(system, new WorkerIterationReference(new WorkerId(workerId), sequence), cancellationToken: cancellationToken);
            return iteration is null ? Results.NotFound() : Results.Ok(iteration);
        });

        group.MapPost("/workers/query", (
            HttpContext httpContext,
            WorkableHttpWorkerCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.Workers(system, query?.ToWorkerCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/iterations/query", (
            HttpContext httpContext,
            WorkableHttpWorkerIterationCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkerIterations(system, query?.ToWorkerIterationCriteria(), cancellationToken: cancellationToken));
        });

        group.MapGet("/workers/status-summary", (
            HttpContext httpContext,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkerStatusSummary(system, cancellationToken: cancellationToken));
        });

        group.MapPost("/workers/status-summary", (
            HttpContext httpContext,
            WorkableHttpWorkerCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkerStatusSummary(system, query?.ToWorkerCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-keys/query", (
            HttpContext httpContext,
            WorkerKeyCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkerKeys(system, query, cancellationToken: cancellationToken));
        });

        group.MapGet("/work-keys/types", (
            HttpContext httpContext,
            WorkKeyKind? kind,
            string? type,
            string? search,
            int? skip,
            int? take,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkerKeyTypes(system, new WorkerKeyTypeCriteria(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkerKeyCriteria.DefaultTake), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-keys/types/query", (
            HttpContext httpContext,
            WorkableHttpWorkerKeyTypeCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkerKeyTypes(system, query?.ToWorkerKeyTypeCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-iteration-keys/query", (
            HttpContext httpContext,
            WorkIterationKeyCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkIterationKeys(system, query, cancellationToken: cancellationToken));
        });

        group.MapGet("/work-iteration-keys/types", (
            HttpContext httpContext,
            WorkKeyKind? kind,
            string? type,
            string? search,
            int? skip,
            int? take,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkIterationKeyTypes(system, new WorkIterationKeyTypeCriteria(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkIterationKeyCriteria.DefaultTake), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-iteration-keys/types/query", (
            HttpContext httpContext,
            WorkableHttpWorkIterationKeyTypeCriteria? query,
            WorkableHttpSystemResolver systems,
            WorkableHttpQueryAdapter queries,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            return WorkableHttpRouteResults.ToOk(queries.WorkIterationKeyTypes(system, query?.ToWorkIterationKeyTypeCriteria(), cancellationToken: cancellationToken));
        });
    }
}
