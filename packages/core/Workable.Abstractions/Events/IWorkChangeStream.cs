namespace Workable;

/// <summary>
/// Creates coalesced subscriptions over current-state change notifications.
/// </summary>
public interface IWorkChangeStream
{
    /// <summary>
    /// Creates a new coalesced change subscription.
    /// </summary>
    /// <param name="options">Optional subscription delivery capacity settings.</param>
    /// <returns>A subscription that can be read asynchronously until it is disposed.</returns>
    IWorkChangeSubscription Subscribe(WorkChangeSubscriptionOptions? options = null);
}
