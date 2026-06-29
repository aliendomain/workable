namespace Workable;

internal sealed class SessionWorkChangeStream(
    WorkChangeStream inner,
    WorkRequestContext requestContext) : IWorkChangeStream
{
    public WorkRequestContext RequestContext { get; } = requestContext;

    public IWorkChangeSubscription Subscribe(WorkChangeSubscriptionOptions? options = null)
        => inner.Subscribe(options);
}
