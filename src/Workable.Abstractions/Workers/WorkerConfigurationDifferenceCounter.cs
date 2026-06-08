using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

/// <summary>
/// Counts how many effective worker settings differ from system or definition defaults.
/// </summary>
public static class WorkerConfigurationDifferenceCounter
{
    private static readonly JsonSerializerOptions ComparisonJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Counts the number of serialized option and configuration fields whose effective values differ from defaults.
    /// </summary>
    /// <param name="currentOptions">The effective worker options to compare.</param>
    /// <param name="currentConfiguration">The effective work configuration to compare.</param>
    /// <param name="defaultOptions">The default worker options to compare against.</param>
    /// <param name="defaultConfiguration">The default work configuration to compare against.</param>
    /// <returns>The number of serialized field differences.</returns>
    public static int CountDifferences(
        WorkerOptions currentOptions,
        WorkConfiguration currentConfiguration,
        WorkerOptions defaultOptions,
        WorkConfiguration defaultConfiguration)
    {
        ArgumentNullException.ThrowIfNull(currentOptions);
        ArgumentNullException.ThrowIfNull(currentConfiguration);
        ArgumentNullException.ThrowIfNull(defaultOptions);
        ArgumentNullException.ThrowIfNull(defaultConfiguration);

        var current = CreateComparison(currentOptions, currentConfiguration);
        var defaults = CreateComparison(defaultOptions, defaultConfiguration);
        return CountDifferences(
            JsonSerializer.SerializeToElement(current, ComparisonJson),
            JsonSerializer.SerializeToElement(defaults, ComparisonJson));
    }

    private static int CountDifferences(JsonElement current, JsonElement defaults)
    {
        if (current.ValueKind != defaults.ValueKind)
        {
            return 1;
        }

        if (current.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = current.EnumerateObject()
                .Select(property => property.Name)
                .Concat(defaults.EnumerateObject().Select(property => property.Name))
                .Distinct(StringComparer.Ordinal);
            var differences = 0;
            foreach (var propertyName in propertyNames)
            {
                var hasCurrent = current.TryGetProperty(propertyName, out var currentProperty);
                var hasDefault = defaults.TryGetProperty(propertyName, out var defaultProperty);
                if (!hasCurrent || !hasDefault)
                {
                    differences++;
                    continue;
                }

                differences += CountDifferences(currentProperty, defaultProperty);
            }

            return differences;
        }

        if (current.ValueKind == JsonValueKind.Array)
        {
            var currentItems = current.EnumerateArray().ToArray();
            var defaultItems = defaults.EnumerateArray().ToArray();
            var differences = Math.Abs(currentItems.Length - defaultItems.Length);
            var sharedLength = Math.Min(currentItems.Length, defaultItems.Length);
            for (var index = 0; index < sharedLength; index++)
            {
                differences += CountDifferences(currentItems[index], defaultItems[index]);
            }

            return differences;
        }

        return current.ToString() == defaults.ToString() ? 0 : 1;
    }

    private static ConfigurationComparison CreateComparison(
        WorkerOptions options,
        WorkConfiguration configuration)
        => new(
            options.ProfilingEnabled,
            configuration.Start,
            configuration.Coordination,
            configuration.Recurrence,
            configuration.TransientRetry,
            configuration.Logging,
            configuration.Retention);

    private sealed record ConfigurationComparison(
        bool ProfilingEnabled,
        WorkStartConfiguration Start,
        WorkCoordinationConfiguration Coordination,
        WorkRecurrenceConfiguration Recurrence,
        WorkTransientRetryConfiguration TransientRetry,
        WorkLoggingConfiguration Logging,
        WorkRetentionConfiguration Retention);
}
