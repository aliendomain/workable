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

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                description: null,
                cancellationToken);
            return Results.Ok(await session.Schedules.List(
                new WorkScheduleCriteria(definitionName, status, resolvedTake),
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
