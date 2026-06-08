namespace Workable;

/// <summary>
/// Observes host-driven system lifecycle transitions.
/// </summary>
public interface IWorkSystemLifecycleObserver
{
    /// <summary>
    /// Called when a system is beginning its shutdown sequence.
    /// </summary>
    /// <param name="system">The system that is stopping.</param>
    /// <param name="origin">The origin metadata associated with the stop request.</param>
    /// <param name="cancellationToken">A token that cancels the observer callback.</param>
    Task SystemStopping(
        IWorkSystem system,
        WorkOrigin origin,
        CancellationToken cancellationToken = default);
}
