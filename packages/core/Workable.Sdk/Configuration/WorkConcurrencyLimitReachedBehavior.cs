using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Determines what happens when a queue request reaches the configured concurrency limit.
/// </summary>
public enum WorkConcurrencyLimitReachedBehavior
{
    /// <summary>
    /// Rejects the queue request instead of materializing a waiting worker.
    /// </summary>
    Ignore,

    /// <summary>
    /// Accepts the worker and leaves it queued until concurrency capacity becomes available.
    /// </summary>
    DeferStart,
}
