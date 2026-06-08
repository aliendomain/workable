namespace Workable;

/// <summary>
/// Filters workers by effective runtime configuration flags.
/// </summary>
/// <param name="RecurrenceEnabled">Whether to include only workers whose definition allows recurrence.</param>
/// <param name="ConcurrencyEnabled">Whether to include only workers whose definition uses concurrency coordination.</param>
/// <param name="ProfilingEnabled">Whether to include only workers whose definition enables profiling.</param>
public sealed record WorkerConfigurationCriteria(
    bool? RecurrenceEnabled = null,
    bool? ConcurrencyEnabled = null,
    bool? ProfilingEnabled = null);
