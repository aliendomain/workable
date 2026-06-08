using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Defines the delay strategy used between transient retry attempts.
/// </summary>
public enum WorkRetryBackoff
{
    /// <summary>
    /// Uses the configured initial delay for every retry attempt.
    /// </summary>
    None,

    /// <summary>
    /// Increases retry delay over time up to the configured maximum delay.
    /// </summary>
    Exponential,
}
