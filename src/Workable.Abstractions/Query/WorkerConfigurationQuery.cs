namespace Workable;

public sealed record WorkerConfigurationQuery(
    bool? RecurrenceEnabled = null,
    bool? ConcurrencyEnabled = null,
    bool? ProfilingEnabled = null);
