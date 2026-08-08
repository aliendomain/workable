namespace Workable;

internal sealed class SessionWorkIterationStatusStream(
    WorkIterationStatusStream inner,
    WorkRequestContext requestContext) : IWorkIterationStatusStream
{
    public WorkRequestContext RequestContext { get; } = requestContext;

    public IWorkIterationStatusSubscription Subscribe(
        WorkerIterationReference iteration,
        long afterSequence = 0)
        => inner.Subscribe(iteration, afterSequence);
}
