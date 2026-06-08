using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures duplicate prevention for workers that share the same definition and <see cref="WorkSubjectId"/>.
/// </summary>
public sealed record WorkIdempotencyConfiguration
{
    /// <summary>
    /// Gets the default idempotency configuration with duplicate prevention disabled.
    /// </summary>
    public static WorkIdempotencyConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether duplicate prevention is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the policy used when another active worker already reserves the same subject.
    /// </summary>
    public WorkIdempotencyConflictPolicy ConflictPolicy { get; init; } = WorkIdempotencyConflictPolicy.RejectDuplicates;
}
