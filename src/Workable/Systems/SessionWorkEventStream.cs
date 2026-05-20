namespace Workable;

internal sealed class SessionWorkEventStream(
    WorkEventStream inner,
    WorkRequestContext requestContext) : IWorkEventStream
{
    public WorkRequestContext RequestContext { get; } = requestContext;

    public IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null)
        => inner.Subscribe(filter, options);
}
