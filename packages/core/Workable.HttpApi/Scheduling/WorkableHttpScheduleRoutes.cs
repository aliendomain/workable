using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpScheduleRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/schedules", async (
            string? definitionName,
            WorkScheduleStatus? status,
            int? take,
            DateTimeOffset? cursorCreatedAt,
            Guid? cursorScheduleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var resolvedTake = take ?? 100;
            if (resolvedTake is < 1 or > WorkScheduleCriteria.MaximumTake)
            {
                return InvalidTake("Schedule list take must be between one and 1000.");
            }

            if (cursorCreatedAt.HasValue != cursorScheduleId.HasValue)
            {
                return InvalidCursor();
            }

            var cursor = cursorCreatedAt is { } createdAt && cursorScheduleId is { } scheduleId
                ? new WorkScheduleCursor(createdAt, new(scheduleId))
                : null;

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: null,
                cancellationToken);
            return Results.Ok(await session.Schedules.List(
                new WorkScheduleCriteria(definitionName, status, resolvedTake, cursor),
                cancellationToken));
        });

        group.MapGet("/schedules/upcoming", async (
            int? take,
            DateTimeOffset? cursorNextRunAt,
            Guid? cursorScheduleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var resolvedTake = take ?? 100;
            if (resolvedTake is < 1 or > WorkScheduleCriteria.MaximumTake)
            {
                return InvalidTake("Upcoming schedule take must be between one and 1000.");
            }

            if (cursorNextRunAt.HasValue != cursorScheduleId.HasValue)
            {
                return InvalidCursor();
            }

            var cursor = cursorNextRunAt is { } nextRunAt && cursorScheduleId is { } scheduleId
                ? new WorkScheduleUpcomingCursor(nextRunAt, new(scheduleId))
                : null;
            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: null,
                cancellationToken);
            return Results.Ok(await session.Schedules.ListUpcoming(
                resolvedTake,
                cursor,
                cancellationToken));
        });

        group.MapGet("/schedules/overview", async (
            string? selectedScheduleId,
            int? recentTake,
            int? upcomingTake,
            int? occurrenceTake,
            DateTimeOffset? recentCursorCreatedAt,
            Guid? recentCursorScheduleId,
            DateTimeOffset? upcomingCursorNextRunAt,
            Guid? upcomingCursorScheduleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            WorkScheduleId? selected = null;
            if (selectedScheduleId is not null)
            {
                if (!TryParseScheduleId(selectedScheduleId, out var parsed, out var invalid))
                {
                    return invalid;
                }

                selected = parsed;
            }

            var resolvedRecentTake = recentTake ?? 100;
            if (resolvedRecentTake is < 1 or > WorkScheduleCriteria.MaximumTake)
            {
                return InvalidTake("Schedule overview recent take must be between one and 1000.");
            }

            var resolvedUpcomingTake = upcomingTake ?? 100;
            if (resolvedUpcomingTake is < 1 or > WorkScheduleCriteria.MaximumTake)
            {
                return InvalidTake("Schedule overview upcoming take must be between one and 1000.");
            }

            var resolvedOccurrenceTake = occurrenceTake ?? 50;
            if (resolvedOccurrenceTake is < 1 or > WorkScheduleOccurrenceReadRequest.MaximumTake)
            {
                return InvalidTake(
                    $"Schedule overview occurrence take must be between one and {WorkScheduleOccurrenceReadRequest.MaximumTake}.");
            }

            if (recentCursorCreatedAt.HasValue != recentCursorScheduleId.HasValue)
            {
                return InvalidCursor(
                    "Schedule overview recentCursorCreatedAt and recentCursorScheduleId must be supplied together.");
            }

            if (upcomingCursorNextRunAt.HasValue != upcomingCursorScheduleId.HasValue)
            {
                return InvalidCursor(
                    "Schedule overview upcomingCursorNextRunAt and upcomingCursorScheduleId must be supplied together.");
            }

            var recentCursor = recentCursorCreatedAt is { } createdAt &&
                recentCursorScheduleId is { } recentScheduleId
                    ? new WorkScheduleCursor(createdAt, new(recentScheduleId))
                    : null;
            var upcomingCursor = upcomingCursorNextRunAt is { } nextRunAt &&
                upcomingCursorScheduleId is { } upcomingScheduleId
                    ? new WorkScheduleUpcomingCursor(nextRunAt, new(upcomingScheduleId))
                    : null;

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: null,
                cancellationToken);
            return Results.Ok(await session.Schedules.GetOverview(
                new(
                    selected,
                    resolvedRecentTake,
                    resolvedUpcomingTake,
                    resolvedOccurrenceTake,
                    recentCursor,
                    upcomingCursor),
                cancellationToken));
        });

        group.MapGet("/schedules/{scheduleId}", async (
            string scheduleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseScheduleId(scheduleId, out var parsed, out var invalid))
            {
                return invalid;
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: null,
                cancellationToken);
            var schedule = await session.Schedules.Get(parsed, cancellationToken);
            return schedule is null ? ScheduleNotFound(parsed) : Results.Ok(schedule);
        });

        group.MapGet("/schedules/{scheduleId}/occurrences", async (
            string scheduleId,
            int? take,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseScheduleId(scheduleId, out var parsed, out var invalid))
            {
                return invalid;
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var resolvedTake = take ?? 100;
            if (resolvedTake is < 1 or > WorkScheduleOccurrenceReadRequest.MaximumTake)
            {
                return InvalidTake($"Schedule occurrence take must be between one and {WorkScheduleOccurrenceReadRequest.MaximumTake}.");
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: null,
                cancellationToken);
            if (await session.Schedules.Get(parsed, cancellationToken) is null)
            {
                return ScheduleNotFound(parsed);
            }

            return Results.Ok(await session.Schedules.ListOccurrences(parsed, resolvedTake, cancellationToken));
        });

        group.MapPost("/schedules/{scheduleId}/cancel", async (
            string scheduleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseScheduleId(scheduleId, out var parsed, out var invalid))
            {
                return invalid;
            }

            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: "Cancel runtime schedule",
                cancellationToken);
            return WorkableHttpRouteResults.ToScheduleCancellationHttpResult(
                await session.Schedules.Cancel(parsed, cancellationToken));
        });

        group.MapPost("/schedules/cron-preview", (
            WorkableHttpCronSchedulePreviewRequest request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out _, out var notFound))
            {
                return notFound;
            }

            if (request.Count is < 1 or > 10)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.schedule.preview_count_invalid",
                            "Cron preview count must be between one and ten.",
                            "count"),
                    },
                });
            }

            var cronExpression = request.CronExpression ?? string.Empty;
            var timeZoneId = request.TimeZoneId ?? string.Empty;
            if (cronExpression.Length > WorkScheduleTiming.MaximumCronExpressionLength)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.schedule.cron_too_long",
                            $"A cron expression cannot exceed {WorkScheduleTiming.MaximumCronExpressionLength} characters.",
                            "cronExpression"),
                    },
                });
            }

            if (timeZoneId.Length > WorkScheduleTiming.MaximumTimeZoneIdLength)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.schedule.time_zone_too_long",
                            $"A time-zone id cannot exceed {WorkScheduleTiming.MaximumTimeZoneIdLength} characters.",
                            "timeZoneId"),
                    },
                });
            }

            var timing = WorkScheduleTiming.Cron(
                cronExpression,
                timeZoneId,
                request.StartsAt ?? DateTimeOffset.UtcNow);
            if (!WorkScheduleTimingCalculator.TryResolveCron(
                    timing,
                    out _,
                    out _,
                    out var error))
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.schedule.cron_invalid",
                            error!,
                            "cronExpression"),
                    },
                });
            }

            var occurrences = WorkScheduleTimingCalculator.GetUpcomingCronOccurrences(timing, request.Count);
            if (occurrences.Count == 0)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.schedule.cron_no_occurrence",
                            "The cron expression has no occurrence on or after the schedule start.",
                            "cronExpression"),
                    },
                });
            }

            return Results.Ok(new WorkableHttpCronSchedulePreviewResponse(occurrences));
        });

        group.MapPost("/work/{name}/schedules", async (
            string name,
            WorkableHttpScheduleRequest request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            if (request.Work?.Completion == WorkableHttpCompletion.WaitForCompletion)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.schedule.completion_not_supported",
                            "A schedule cannot wait for a future worker to complete.",
                            "work.completion"),
                    },
                });
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                request.Work?.Description,
                cancellationToken);
            var outcome = await session.Schedules.Create(
                new WorkScheduleRequest(
                    name,
                    request.Timing,
                    WorkableHttpQueueAdapter.CreateInput(request.Work),
                    request.Work?.Options?.ToWorkerOptions()),
                cancellationToken);
            return WorkableHttpRouteResults.ToScheduleCreationHttpResult(outcome);
        });
    }

    private static bool TryParseScheduleId(
        string value,
        out WorkScheduleId scheduleId,
        out IResult invalid)
    {
        if (Guid.TryParse(value, out var parsed))
        {
            scheduleId = new(parsed);
            invalid = Results.BadRequest();
            return true;
        }

        scheduleId = default;
        invalid = Results.BadRequest(new
        {
            Messages = new[]
            {
                WorkMessage.Error(
                    "workable.schedule.id_invalid",
                    "The schedule id must be a GUID.",
                    "scheduleId"),
            },
        });
        return false;
    }

    private static IResult InvalidTake(string message)
        => Results.BadRequest(new
        {
            Messages = new[]
            {
                WorkMessage.Error("workable.schedule.take_invalid", message, "take"),
            },
        });

    private static IResult InvalidCursor(
        string message = "Schedule list cursorCreatedAt and cursorScheduleId must be supplied together.")
        => Results.BadRequest(new
        {
            Messages = new[]
            {
                WorkMessage.Error(
                    "workable.schedule.cursor_invalid",
                    message,
                    "cursor"),
            },
        });

    private static IResult ScheduleNotFound(WorkScheduleId scheduleId)
        => Results.NotFound(new
        {
            Messages = new[]
            {
                WorkMessage.Error(
                    "workable.schedule.not_found",
                    $"Schedule '{scheduleId}' was not found.",
                    "scheduleId"),
            },
        });
}
