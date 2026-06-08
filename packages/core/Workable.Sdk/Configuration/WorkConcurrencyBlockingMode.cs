using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Determines which worker states continue occupying configured concurrency capacity.
/// </summary>
public enum WorkConcurrencyBlockingMode
{
    /// <summary>
    /// Holds capacity while a worker is executing, paused, or failed.
    /// </summary>
    WhileExecutingPausedOrFailed,

    /// <summary>
    /// Holds capacity while a worker is executing or paused.
    /// </summary>
    WhileExecutingOrPaused,

    /// <summary>
    /// Holds capacity while a worker is executing or failed.
    /// </summary>
    WhileExecutingOrFailed,

    /// <summary>
    /// Holds capacity only while a worker is actively executing.
    /// </summary>
    WhileExecuting,
}
