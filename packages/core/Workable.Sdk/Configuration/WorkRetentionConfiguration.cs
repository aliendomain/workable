using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Configures retention and eventual purge of completed or canceled workers.
/// </summary>
public sealed record WorkRetentionConfiguration
{
    /// <summary>
    /// Gets the default retention configuration.
    /// </summary>
    public static WorkRetentionConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the maximum age a completed or canceled worker can remain available before purge.
    /// </summary>
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets the target number of completed or canceled workers retained for the definition.
    /// </summary>
    public int MaximumFinalWorkers { get; init; } = 1_000;
}
