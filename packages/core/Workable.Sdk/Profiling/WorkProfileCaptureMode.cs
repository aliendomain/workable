namespace Workable;

/// <summary>
/// Controls how automatic instrumentation contributes nodes to a worker profile.
/// </summary>
public enum WorkProfileCaptureMode
{
    /// <summary>
    /// Applies the configured per-profile automatic instrumentation limit.
    /// </summary>
    Bounded,

    /// <summary>
    /// Captures all automatic instrumentation for the worker.
    /// </summary>
    Full,
}
