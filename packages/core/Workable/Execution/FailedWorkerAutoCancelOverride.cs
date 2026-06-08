namespace Workable;

internal enum FailedWorkerAutoCancelOverrideMode
{
    None = 0,
    Manual = 1,
    Configured = 2,
    Explicit = 3,
}

internal readonly record struct FailedWorkerAutoCancelOverride(
    FailedWorkerAutoCancelOverrideMode Mode,
    TimeSpan? AutoCancelAfter = null)
{
    public static FailedWorkerAutoCancelOverride None { get; } =
        new(FailedWorkerAutoCancelOverrideMode.None);

    public static FailedWorkerAutoCancelOverride Manual { get; } =
        new(FailedWorkerAutoCancelOverrideMode.Manual);

    public static FailedWorkerAutoCancelOverride Configured { get; } =
        new(FailedWorkerAutoCancelOverrideMode.Configured);

    public static FailedWorkerAutoCancelOverride Explicit(TimeSpan autoCancelAfter)
        => new(FailedWorkerAutoCancelOverrideMode.Explicit, autoCancelAfter);
}
