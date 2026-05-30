using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Workable;

internal static class WorkableHttpQueryRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/components/query", (
            HttpContext httpContext,
            WorkComponentCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query Workable components through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.Components(session, query, cancellationToken: cancellationToken));
        });

        group.MapPost("/components/{componentName}", (
            HttpContext httpContext,
            string componentName,
            WorkSingleComponentCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a Workable component through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.Components(session, new WorkComponentCriteria(
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
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, $"Query Workable view '{viewName}' through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.View(session, viewName, query, cancellationToken: cancellationToken));
        });

        group.MapPost("/definitions/query", async (
            HttpContext httpContext,
            WorkDefinitionCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query work definitions through HTTP API.");
            var definitions = await queries.WorkDefinitions(session, query, cancellationToken: cancellationToken);
            return Results.Ok(definitions);
        });

        group.MapGet("/definitions/{definitionId:guid}/info", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query work information through HTTP API.");
            var info = await queries.DefinitionInfo(session, system, new WorkDefinitionId(definitionId), cancellationToken: cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/work/id/{definitionId:guid}/info", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query work information through HTTP API.");
            var info = await queries.DefinitionInfo(session, system, new WorkDefinitionId(definitionId), cancellationToken: cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/work/{name}/info", async (
            HttpContext httpContext,
            string name,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, $"Query work '{name}' information through HTTP API.");
            var info = await queries.DefinitionInfo(session, system, name, cancellationToken: cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        group.MapGet("/workers/{workerId:guid}", async (
            HttpContext httpContext,
            Guid workerId,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a worker through HTTP API.");
            var worker = await queries.Worker(session, new WorkerId(workerId), cancellationToken: cancellationToken);
            return worker is null ? Results.NotFound() : Results.Ok(worker);
        });

        group.MapGet("/workers/{workerId:guid}/configuration", async (
            HttpContext httpContext,
            Guid workerId,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker configuration through HTTP API.");
            var configuration = await queries.WorkerConfiguration(session, system, new WorkerId(workerId), cancellationToken: cancellationToken);
            return configuration is null ? Results.NotFound() : Results.Ok(configuration);
        });

        group.MapGet("/workers/{workerId:guid}/overview", async (
            HttpContext httpContext,
            Guid workerId,
            WorkWorkerOverviewActivity? activity,
            int? activityTake,
            string? activityCursor,
            int? recentIterationTake,
            WorkWorkerOverviewSortDirection? logSort,
            string? logLevels,
            long? logIterationSequence,
            WorkWorkerOverviewSortDirection? timelineSort,
            string? timelineCategories,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a worker overview snapshot through HTTP API.");
            var landing = await queries.WorkerOverview(
                session,
                new WorkerId(workerId),
                new WorkWorkerOverviewCriteria(
                    activity ?? WorkWorkerOverviewActivity.Auto,
                    activityTake ?? 50,
                    activityCursor,
                    recentIterationTake ?? 25,
                    logSort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseLogLevels(logLevels),
                    logIterationSequence,
                    timelineSort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseTimelineCategories(timelineCategories)),
                cancellationToken: cancellationToken);
            return landing is null ? Results.NotFound() : Results.Ok(landing);
        });

        group.MapGet("/workers/{workerId:guid}/overview/logs", async (
            HttpContext httpContext,
            Guid workerId,
            int? activityTake,
            string? activityCursor,
            WorkWorkerOverviewSortDirection? logSort,
            string? logLevels,
            long? logIterationSequence,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a worker log overview snapshot through HTTP API.");
            var logs = await queries.WorkerOverviewLogs(
                session,
                new WorkerId(workerId),
                new WorkWorkerOverviewCriteria(
                    WorkWorkerOverviewActivity.Logs,
                    activityTake ?? 50,
                    activityCursor,
                    25,
                    logSort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseLogLevels(logLevels),
                    logIterationSequence,
                    WorkWorkerOverviewSortDirection.Desc,
                    null),
                cancellationToken: cancellationToken);
            return logs is null ? Results.NotFound() : Results.Ok(logs);
        });

        group.MapGet("/workers/{workerId:guid}/overview/timeline", async (
            HttpContext httpContext,
            Guid workerId,
            int? activityTake,
            string? activityCursor,
            WorkWorkerOverviewSortDirection? timelineSort,
            string? timelineCategories,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a worker timeline overview snapshot through HTTP API.");
            var timeline = await queries.WorkerOverviewTimeline(
                session,
                new WorkerId(workerId),
                new WorkWorkerOverviewCriteria(
                    WorkWorkerOverviewActivity.Timeline,
                    activityTake ?? 50,
                    activityCursor,
                    25,
                    WorkWorkerOverviewSortDirection.Desc,
                    null,
                    null,
                    timelineSort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseTimelineCategories(timelineCategories)),
                cancellationToken: cancellationToken);
            return timeline is null ? Results.NotFound() : Results.Ok(timeline);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a worker iteration through HTTP API.");
            var iteration = await queries.WorkerIteration(session, new WorkerIterationReference(new WorkerId(workerId), sequence), cancellationToken: cancellationToken);
            return iteration is null ? Results.NotFound() : Results.Ok(iteration);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}/detail", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query a worker iteration detail snapshot through HTTP API.");
            var detail = await queries.WorkerIterationDetail(session, new WorkerId(workerId), sequence, cancellationToken: cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}/messages", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            int? take,
            string? cursor,
            WorkWorkerOverviewSortDirection? sort,
            string? severities,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker iteration messages through HTTP API.");
            var messages = await queries.WorkerIterationMessages(
                session,
                new WorkerIterationReference(new WorkerId(workerId), sequence),
                new WorkIterationMessageCriteria(
                    take ?? 50,
                    cursor,
                    sort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseMessageSeverities(severities)),
                cancellationToken: cancellationToken);
            return messages is null ? Results.NotFound() : Results.Ok(messages);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}/logs", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            int? take,
            string? cursor,
            WorkWorkerOverviewSortDirection? sort,
            string? logLevels,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker iteration logs through HTTP API.");
            var logs = await queries.WorkerIterationLogs(
                session,
                new WorkerIterationReference(new WorkerId(workerId), sequence),
                new WorkIterationLogCriteria(
                    take ?? 50,
                    cursor,
                    sort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseLogLevels(logLevels)),
                cancellationToken: cancellationToken);
            return logs is null ? Results.NotFound() : Results.Ok(logs);
        });

        group.MapGet("/workers/status-summary", (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker status summary through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.WorkerStatusSummary(session, cancellationToken: cancellationToken));
        });

        group.MapPost("/workers/status-summary", (
            HttpContext httpContext,
            WorkableHttpWorkerCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker status summary through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.WorkerStatusSummary(session, query?.ToWorkerCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-keys/query", (
            HttpContext httpContext,
            WorkerKeyCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker keys through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.WorkerKeys(session, query, cancellationToken: cancellationToken));
        });

        group.MapGet("/work-keys/types", (
            HttpContext httpContext,
            WorkKeyKind? kind,
            string? type,
            string? search,
            int? skip,
            int? take,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker key types through HTTP API.");
            return WorkableHttpRouteResults.ToOk(() => queries.WorkerKeyTypes(session, new WorkerKeyTypeCriteria(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkerKeyCriteria.DefaultTake), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-keys/types/query", (
            HttpContext httpContext,
            WorkableHttpWorkerKeyTypeCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query worker key types through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.WorkerKeyTypes(session, query?.ToWorkerKeyTypeCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-iteration-keys/query", (
            HttpContext httpContext,
            WorkIterationKeyCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query work iteration keys through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.WorkIterationKeys(session, query, cancellationToken: cancellationToken));
        });

        group.MapGet("/work-iteration-keys/types", (
            HttpContext httpContext,
            WorkKeyKind? kind,
            string? type,
            string? search,
            int? skip,
            int? take,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query work iteration key types through HTTP API.");
            return WorkableHttpRouteResults.ToOk(() => queries.WorkIterationKeyTypes(session, new WorkIterationKeyTypeCriteria(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkIterationKeyCriteria.DefaultTake), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-iteration-keys/types/query", (
            HttpContext httpContext,
            WorkableHttpWorkIterationKeyTypeCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return Task.FromResult(notFound);
            }

            var session = WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts, "Query work iteration key types through HTTP API.");
            return WorkableHttpRouteResults.ToOk(
                () => queries.WorkIterationKeyTypes(session, query?.ToWorkIterationKeyTypeCriteria(), cancellationToken: cancellationToken));
        });
    }

    private static IReadOnlyList<LogLevel>? ParseLogLevels(string? value)
    {
        var levels = ParseCsvEnum<LogLevel>(value);
        return levels.Count == 0 ? null : levels;
    }

    private static IReadOnlyList<WorkMessageSeverity>? ParseMessageSeverities(string? value)
    {
        var severities = ParseCsvEnum<WorkMessageSeverity>(value);
        return severities.Count == 0 ? null : severities;
    }

    private static IReadOnlyList<WorkWorkerOverviewTimelineCategory>? ParseTimelineCategories(string? value)
    {
        var categories = ParseCsvEnum<WorkWorkerOverviewTimelineCategory>(value);
        return categories.Count == 0 ? null : categories;
    }

    private static IReadOnlyList<TEnum> ParseCsvEnum<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parsed = new List<TEnum>();
        foreach (var segment in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<TEnum>(segment, ignoreCase: true, out var enumValue) &&
                !parsed.Contains(enumValue))
            {
                parsed.Add(enumValue);
            }
        }

        return parsed;
    }
}

