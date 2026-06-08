using System.Diagnostics.CodeAnalysis;

namespace Workable;
/// <summary>
/// Publishes worker lifecycle and related operational events.
/// </summary>
public interface IWorkEventStream
{
    /// <summary>
    /// Creates a new event subscription for the supplied filter and buffering options.
    /// </summary>
    /// <param name="filter">Optional event filters that restrict which events are delivered to the subscription.</param>
    /// <param name="options">Optional subscription buffering and overflow settings.</param>
    /// <returns>A subscription that can be read asynchronously until it is disposed.</returns>
    IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null);
}
