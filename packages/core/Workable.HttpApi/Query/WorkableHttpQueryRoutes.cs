using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Workable;

internal static class WorkableHttpQueryRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/components/query", async (
            HttpContext httpContext,
            WorkComponentCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.Components(session, query, cancellationToken: cancellationToken));
        });

        group.MapPost("/components/{componentName}", async (
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
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.Components(session, new WorkComponentCriteria(
                    query?.Scope,
                    [new WorkComponentRequest(
                        componentName,
                        componentName,
                        query?.Options,
                        query?.Shape ?? WorkComponentShapes.Detailed)]), cancellationToken: cancellationToken));
        });

        group.MapPost("/views/{viewName}", async (
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
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            var definitions = await queries.WorkDefinitions(session, query, cancellationToken: cancellationToken);
            return Results.Ok(definitions);
        });

        group.MapGet("/definitions/{name}/info", async (
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            var info = await queries.DefinitionInfo(session, system, name, cancellationToken: cancellationToken);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            var iteration = await queries.WorkerIteration(session, new WorkerIterationReference(new WorkerId(workerId), sequence), cancellationToken: cancellationToken);
            return iteration is null ? Results.NotFound() : Results.Ok(iteration);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}/overview", async (
            HttpContext httpContext,
            Guid workerId,
            long sequence,
            WorkWorkerIterationOverviewActivity? activity,
            int? activityTake,
            string? activityCursor,
            bool? includeInput,
            bool? includeOutput,
            bool? includeProfile,
            WorkWorkerOverviewSortDirection? messageSort,
            string? severities,
            WorkWorkerOverviewSortDirection? logSort,
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            var overview = await queries.WorkerIterationOverview(
                session,
                new WorkerId(workerId),
                sequence,
                new WorkWorkerIterationOverviewCriteria(
                    activity ?? WorkWorkerIterationOverviewActivity.Auto,
                    activityTake ?? 50,
                    activityCursor,
                    includeInput ?? true,
                    includeOutput ?? true,
                    includeProfile ?? true,
                    messageSort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseMessageSeverities(severities),
                    logSort ?? WorkWorkerOverviewSortDirection.Desc,
                    ParseLogLevels(logLevels)),
                cancellationToken: cancellationToken);
            return overview is null ? Results.NotFound() : Results.Ok(overview);
        });

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}/overview/messages", async (
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

        group.MapGet("/workers/{workerId:guid}/iterations/{sequence:long}/overview/logs", async (
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

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
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

        group.MapGet("/workers/status-summary", async (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.WorkerStatusSummary(session, cancellationToken: cancellationToken));
        });

        group.MapPost("/workers/status-summary", async (
            HttpContext httpContext,
            WorkableHttpWorkerCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.WorkerStatusSummary(session, query?.ToWorkerCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-keys/query", async (
            HttpContext httpContext,
            WorkerKeyCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.WorkerKeys(session, query, cancellationToken: cancellationToken));
        });

        group.MapGet("/work-keys/types", async (
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
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(() => queries.WorkerKeyTypes(session, new WorkerKeyTypeCriteria(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkerKeyCriteria.DefaultTake), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-keys/types/query", async (
            HttpContext httpContext,
            WorkableHttpWorkerKeyTypeCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.WorkerKeyTypes(session, query?.ToWorkerKeyTypeCriteria(), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-iteration-keys/query", async (
            HttpContext httpContext,
            WorkIterationKeyCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
                () => queries.WorkIterationKeys(session, query, cancellationToken: cancellationToken));
        });

        group.MapGet("/work-iteration-keys/types", async (
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
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(() => queries.WorkIterationKeyTypes(session, new WorkIterationKeyTypeCriteria(
                    Kind: kind,
                    Search: search,
                    Type: type,
                    Skip: skip ?? 0,
                    Take: take ?? WorkIterationKeyCriteria.DefaultTake), cancellationToken: cancellationToken));
        });

        group.MapPost("/work-iteration-keys/types/query", async (
            HttpContext httpContext,
            WorkableHttpWorkIterationKeyTypeCriteria? query,
            WorkableHttpTopologyResolver topology,
            WorkableHttpQueryAdapter queries,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(httpContext, system, requestContexts);
            return await WorkableHttpRouteResults.ToOk(
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
