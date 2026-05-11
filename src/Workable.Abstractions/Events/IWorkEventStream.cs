using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkEventStream
{
    IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null);
}
