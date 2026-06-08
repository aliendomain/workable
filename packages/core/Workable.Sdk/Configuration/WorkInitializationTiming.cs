namespace Workable;

/// <summary>
/// Controls when a registered initializer runs relative to worker lifecycle.
/// </summary>
public enum WorkInitializationTiming
{
    /// <summary>
    /// Run the initializer once for each worker execution attempt.
    /// </summary>
    OncePerWorker,

    /// <summary>
    /// Run the initializer once lazily per definition and reuse the successful result for later workers.
    /// </summary>
    OnceLazy,
}
