namespace Workable;

internal sealed record WorkSystemShutdownGracePeriod
{
    public const double DefaultHostShutdownTimeoutRatio = 0.8;
    public const double MaximumHostShutdownTimeoutRatio = 0.9;
    public static readonly TimeSpan FallbackGracePeriod = TimeSpan.FromSeconds(15);

    private WorkSystemShutdownGracePeriod(
        TimeSpan? explicitGracePeriod,
        double hostShutdownTimeoutRatio)
    {
        this.ExplicitGracePeriod = explicitGracePeriod;
        this.HostShutdownTimeoutRatio = hostShutdownTimeoutRatio;
    }

    public TimeSpan? ExplicitGracePeriod { get; }

    public double HostShutdownTimeoutRatio { get; }

    public static WorkSystemShutdownGracePeriod HostRelative(
        double ratio = DefaultHostShutdownTimeoutRatio)
    {
        ValidateRatio(ratio, nameof(ratio));
        return new WorkSystemShutdownGracePeriod(null, ratio);
    }

    public static WorkSystemShutdownGracePeriod HostRelative(
        double ratio,
        string paramName)
    {
        ValidateRatio(ratio, paramName);
        return new WorkSystemShutdownGracePeriod(null, ratio);
    }

    public static WorkSystemShutdownGracePeriod Explicit(TimeSpan gracePeriod)
    {
        if (gracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gracePeriod),
                gracePeriod,
                "Shutdown grace period cannot be negative.");
        }

        return new WorkSystemShutdownGracePeriod(
            gracePeriod,
            DefaultHostShutdownTimeoutRatio);
    }

    public TimeSpan Resolve(TimeSpan? hostShutdownTimeout)
    {
        if (this.ExplicitGracePeriod is { } explicitGracePeriod)
        {
            return explicitGracePeriod;
        }

        if (hostShutdownTimeout is not { } shutdownTimeout || shutdownTimeout <= TimeSpan.Zero)
        {
            return FallbackGracePeriod;
        }

        return TimeSpan.FromTicks(
            Math.Max(0, (long)(shutdownTimeout.Ticks * this.HostShutdownTimeoutRatio)));
    }

    private static void ValidateRatio(double ratio, string paramName)
    {
        if (!double.IsFinite(ratio) ||
            ratio <= 0 ||
            ratio > MaximumHostShutdownTimeoutRatio)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                ratio,
                $"Shutdown grace period ratio must be greater than 0 and less than or equal to {MaximumHostShutdownTimeoutRatio:P0}.");
        }
    }
}
