using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Determines whether manual worker starts can override concurrency capacity checks.
/// </summary>
public enum WorkConcurrencyOverrideBehavior
{
    /// <summary>
    /// Allows manual starts to bypass the configured capacity limit.
    /// </summary>
    Flexible,

    /// <summary>
    /// Requires capacity to be available even for manual starts.
    /// </summary>
    Strict,
}
