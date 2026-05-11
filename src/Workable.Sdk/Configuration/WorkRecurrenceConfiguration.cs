using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkRecurrenceConfiguration
{
    public static WorkRecurrenceConfiguration Default { get; } = new();

    public static WorkRecurrenceConfiguration Disabled { get; } = Default;

    public static WorkRecurrenceConfiguration Every(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Recurrence interval must be greater than zero.");
        }

        return Default with
        {
            IsEnabled = true,
            Interval = interval,
        };
    }

    public bool IsEnabled { get; init; }

    public TimeSpan Interval { get; init; } = TimeSpan.Zero;

    public bool ContinueAfterFailure { get; init; } = true;

    public int CircuitBreakerFailureThreshold { get; init; } = 3;

    public int MaximumSuccessfulIterations { get; init; } = 25;

    public int MaximumFailedIterations { get; init; } = 5;

    public bool RaiseCircuitBreakerOpenedEvent { get; init; } = true;
}
