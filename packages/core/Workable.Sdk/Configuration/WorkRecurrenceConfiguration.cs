using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures repeated execution of the same worker across multiple iterations.
/// </summary>
public sealed record WorkRecurrenceConfiguration
{
    private readonly int retainedIterations = 25;

    /// <summary>
    /// Gets the default recurrence configuration with recurrence disabled.
    /// </summary>
    public static WorkRecurrenceConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a recurrence configuration that disables recurrence.
    /// </summary>
    public static WorkRecurrenceConfiguration Disabled { get; } = Default;

    /// <summary>
    /// Creates an enabled recurrence configuration for the specified interval.
    /// </summary>
    /// <param name="interval">The wait time between completed iterations.</param>
    /// <returns>An enabled recurrence configuration.</returns>
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

    /// <summary>
    /// Gets a value indicating whether recurrence is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the wait time between completed iterations.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gets a value indicating whether recurrence should continue after a failed iteration.
    /// </summary>
    public bool ContinueAfterFailure { get; init; } = true;

    /// <summary>
    /// Gets the number of consecutive failed iterations that opens the recurrence circuit breaker.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; init; } = 3;

    /// <summary>
    /// Gets the number of iteration records retained on the worker snapshot.
    /// </summary>
    public int RetainedIterations
    {
        get => this.retainedIterations;
        init => this.retainedIterations = value;
    }

    /// <summary>
    /// Gets a value indicating whether Workable publishes an event when the recurrence circuit breaker opens.
    /// </summary>
    public bool RaiseCircuitBreakerOpenedEvent { get; init; } = true;
}
