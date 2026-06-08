namespace Workable;

/// <summary>
/// Produces work requests that Workable should queue when a system starts.
/// </summary>
/// <remarks>
/// Workable resolves and calls the source each time a stopped system starts. The returned requests are queued
/// after runtime work definitions are available and before startup completes.
/// </remarks>
public interface IStartupWorkSource
{
    /// <summary>
    /// Creates the startup work requests to queue for the current system start.
    /// </summary>
    /// <param name="cancellationToken">A token that is canceled when system startup is being aborted.</param>
    /// <returns>
    /// A task that returns the requests to queue. Each request should refer to a definition that will exist
    /// in the starting system.
    /// </returns>
    Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default);
}
