using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Controls when accepted work starts and how long queue acceptance waits before returning.
/// </summary>
public enum WorkStartPolicy
{
    /// <summary>
    /// Accept the worker without starting it automatically.
    /// </summary>
    DoNotStart,

    /// <summary>
    /// Start the worker automatically and return after acceptance.
    /// </summary>
    StartAndReturnAfterAccepted,

    /// <summary>
    /// Start the worker automatically and wait until it has actually started.
    /// </summary>
    StartAndReturnAfterStarted,

    /// <summary>
    /// Start the worker automatically and wait until it reaches terminal completion.
    /// </summary>
    StartAndReturnAfterCompleted,
}
