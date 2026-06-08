using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Declares default subject-based duplicate prevention for a work executor.
/// </summary>
/// <remarks>
/// Workable reads this attribute during registration and applies its configuration before fluent registration
/// overrides. When enabled, queue requests must provide a <see cref="WorkSubjectId"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkIdempotencyAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkIdempotencyAttribute"/> class.
    /// </summary>
    /// <param name="isEnabled">Enables idempotency for the executor's work definition.</param>
    /// <param name="conflictPolicy">The policy used when another active worker already owns the same subject.</param>
    public WorkIdempotencyAttribute(
        bool isEnabled = true,
        WorkIdempotencyConflictPolicy conflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates)
    {
        this.Configuration = new WorkIdempotencyConfiguration
        {
            IsEnabled = isEnabled,
            ConflictPolicy = conflictPolicy,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = isEnabled,
                Idempotency = this.Configuration,
            },
        });
    }

    /// <summary>
    /// Gets the validated idempotency configuration produced by the attribute.
    /// </summary>
    public WorkIdempotencyConfiguration Configuration { get; }
}
