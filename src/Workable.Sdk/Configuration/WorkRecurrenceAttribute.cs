using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Declares default recurrence behavior for a work executor.
/// </summary>
/// <remarks>
/// Workable applies this attribute during registration and validates the resulting recurrence configuration before
/// any fluent registration overrides are applied.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkRecurrenceAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRecurrenceAttribute"/> class with default failure behavior.
    /// </summary>
    /// <param name="intervalMilliseconds">The recurrence interval, in milliseconds, between completed iterations.</param>
    public WorkRecurrenceAttribute(int intervalMilliseconds)
        : this(
            intervalMilliseconds: intervalMilliseconds,
            continueAfterFailure: true,
            circuitBreakerFailureThreshold: 3,
            retainedIterations: 25,
            raiseCircuitBreakerOpenedEvent: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkRecurrenceAttribute"/> class.
    /// </summary>
    /// <param name="intervalMilliseconds">The recurrence interval, in milliseconds, between completed iterations.</param>
    /// <param name="continueAfterFailure">
    /// Continues the recurring loop after a failed iteration while the circuit breaker remains closed.
    /// </param>
    /// <param name="circuitBreakerFailureThreshold">
    /// The number of consecutive failed iterations that opens the recurrence circuit breaker.
    /// </param>
    /// <param name="retainedIterations">The number of iteration records retained on the worker snapshot.</param>
    /// <param name="raiseCircuitBreakerOpenedEvent">
    /// Publishes an event when recurrence stops because the circuit breaker opens.
    /// </param>
    public WorkRecurrenceAttribute(
        int intervalMilliseconds,
        bool continueAfterFailure = true,
        int circuitBreakerFailureThreshold = 3,
        int retainedIterations = 25,
        bool raiseCircuitBreakerOpenedEvent = true)
    {
        this.Configuration = new WorkRecurrenceConfiguration
        {
            IsEnabled = true,
            Interval = TimeSpan.FromMilliseconds(intervalMilliseconds),
            ContinueAfterFailure = continueAfterFailure,
            CircuitBreakerFailureThreshold = circuitBreakerFailureThreshold,
            RetainedIterations = retainedIterations,
            RaiseCircuitBreakerOpenedEvent = raiseCircuitBreakerOpenedEvent,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Recurrence = this.Configuration });
    }

    /// <summary>
    /// Gets the validated recurrence configuration produced by the attribute.
    /// </summary>
    public WorkRecurrenceConfiguration Configuration { get; }
}
