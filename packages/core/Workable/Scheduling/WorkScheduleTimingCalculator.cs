using Cronos;

namespace Workable;

internal static class WorkScheduleTimingCalculator
{
    internal static DateTimeOffset? GetInitialRunAt(WorkScheduleTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        return timing.IsCron
            ? GetCronOccurrence(timing, timing.FirstRunAt, inclusive: true)
            : timing.FirstRunAt;
    }

    internal static DateTimeOffset? GetNextRunAt(
        WorkScheduleTiming timing,
        DateTimeOffset scheduledAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.IsCron)
        {
            return GetCronOccurrence(timing, now > scheduledAt ? now : scheduledAt, inclusive: false);
        }

        if (timing.Interval is not { } interval)
        {
            return null;
        }

        try
        {
            var intervalTicks = interval.Ticks;
            var elapsedTicks = Math.Max(0, (now - scheduledAt).Ticks);
            var occurrences = (elapsedTicks / intervalTicks) + 1;
            return scheduledAt.AddTicks(checked(occurrences * intervalTicks));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    internal static bool TryResolveCron(
        WorkScheduleTiming timing,
        out CronExpression? expression,
        out TimeZoneInfo? timeZone,
        out string? error)
    {
        expression = null;
        timeZone = null;
        error = null;

        if (string.IsNullOrWhiteSpace(timing.CronExpression))
        {
            error = "A cron expression is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timing.TimeZoneId))
        {
            error = "A time zone is required for a cron schedule.";
            return false;
        }

        try
        {
            expression = CronExpression.Parse(timing.CronExpression.Trim(), CronFormat.Standard);
        }
        catch (CronFormatException)
        {
            error = "The cron expression must use the standard five-field format.";
            return false;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timing.TimeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            error = $"The time zone '{timing.TimeZoneId}' was not found.";
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            error = $"The time zone '{timing.TimeZoneId}' is invalid.";
            return false;
        }

        return true;
    }

    internal static IReadOnlyList<DateTimeOffset> GetUpcomingCronOccurrences(
        WorkScheduleTiming timing,
        int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var occurrences = new List<DateTimeOffset>(count);
        var cursor = timing.FirstRunAt;
        for (var index = 0; index < count; index++)
        {
            var occurrence = GetCronOccurrence(timing, cursor, inclusive: index == 0);
            if (occurrence is null)
            {
                break;
            }

            occurrences.Add(occurrence.Value);
            cursor = occurrence.Value;
        }

        return occurrences;
    }

    private static DateTimeOffset? GetCronOccurrence(
        WorkScheduleTiming timing,
        DateTimeOffset from,
        bool inclusive)
    {
        if (!TryResolveCron(timing, out var expression, out var timeZone, out _))
        {
            return null;
        }

        return expression!.GetNextOccurrence(from, timeZone!, inclusive);
    }
}
