namespace Workable;

public sealed record WorkerConfigurationCriteria(
    bool? RecurrenceEnabled = null,
    bool? ConcurrencyEnabled = null,
    bool? ProfilingEnabled = null);
