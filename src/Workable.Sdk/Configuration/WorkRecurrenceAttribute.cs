using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkRecurrenceAttribute : Attribute
{
    public WorkRecurrenceAttribute(int intervalMilliseconds)
        : this(
            intervalMilliseconds: intervalMilliseconds,
            continueAfterFailure: true,
            circuitBreakerFailureThreshold: 3,
            maximumSuccessfulIterations: 25,
            maximumFailedIterations: 5,
            raiseCircuitBreakerOpenedEvent: true)
    {
    }

    public WorkRecurrenceAttribute(
        int intervalMilliseconds,
        bool continueAfterFailure = true,
        int circuitBreakerFailureThreshold = 3,
        int maximumSuccessfulIterations = 25,
        int maximumFailedIterations = 5,
        bool raiseCircuitBreakerOpenedEvent = true)
    {
        this.Configuration = new WorkRecurrenceConfiguration
        {
            IsEnabled = true,
            Interval = TimeSpan.FromMilliseconds(intervalMilliseconds),
            ContinueAfterFailure = continueAfterFailure,
            CircuitBreakerFailureThreshold = circuitBreakerFailureThreshold,
            MaximumSuccessfulIterations = maximumSuccessfulIterations,
            MaximumFailedIterations = maximumFailedIterations,
            RaiseCircuitBreakerOpenedEvent = raiseCircuitBreakerOpenedEvent,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Recurrence = this.Configuration });
    }

    public WorkRecurrenceConfiguration Configuration { get; }
}
