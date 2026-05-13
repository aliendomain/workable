using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkRecurrenceConfiguration
{
    private readonly int retainedSuccessfulIterations = 25;
    private readonly int retainedFailedIterations = 5;

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

    public int RetainedSuccessfulIterations
    {
        get => this.retainedSuccessfulIterations;
        init => this.retainedSuccessfulIterations = value;
    }

    public int RetainedFailedIterations
    {
        get => this.retainedFailedIterations;
        init => this.retainedFailedIterations = value;
    }

    [Obsolete("Use RetainedSuccessfulIterations.")]
    [JsonIgnore]
    public int MaximumSuccessfulIterations
    {
        get => this.retainedSuccessfulIterations;
        init => this.retainedSuccessfulIterations = value;
    }

    [Obsolete("Use RetainedFailedIterations.")]
    [JsonIgnore]
    public int MaximumFailedIterations
    {
        get => this.retainedFailedIterations;
        init => this.retainedFailedIterations = value;
    }

    public bool RaiseCircuitBreakerOpenedEvent { get; init; } = true;
}
