namespace Workable;
public sealed record WorkerOptions(
    bool ProfilingEnabled = false,
    WorkConfiguration? Configuration = null)
{
    public static WorkerOptions Default { get; } = new();

    public WorkerOptions Merge(WorkerOptions? overrides)
        => overrides is null
            ? this
            : this with
            {
                ProfilingEnabled = overrides.ProfilingEnabled,
                Configuration = this.Configuration?.MergeRuntimeOptions(overrides.Configuration) ?? overrides.Configuration,
            };
}
