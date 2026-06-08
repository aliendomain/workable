using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Controls whether queue acceptance starts work automatically and how long acceptance waits before returning.
/// </summary>
public sealed record WorkStartConfiguration
{
    /// <summary>
    /// Gets the default start configuration.
    /// </summary>
    public static WorkStartConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a configuration that accepts work without starting it automatically.
    /// </summary>
    public static WorkStartConfiguration DoNotStart { get; } = new()
    {
        Policy = WorkStartPolicy.DoNotStart,
    };

    /// <summary>
    /// Gets the start policy applied to the worker.
    /// </summary>
    public WorkStartPolicy Policy { get; init; } = WorkStartPolicy.StartAndReturnAfterAccepted;
}
