namespace Workable;

public sealed record WorkRequestContext(
    WorkActor Actor,
    WorkOrigin Origin,
    WorkAuthorizationSnapshot? Authorization = null)
{
    public WorkRequestContext(WorkOrigin origin)
        : this(origin?.Actor ?? throw new ArgumentNullException(nameof(origin)), origin, null)
    {
    }

    public static WorkRequestContext Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null,
        string? description = null,
        string? url = null)
    {
        var resolvedActor = actor ?? WorkActor.Unknown;
        return new WorkRequestContext(
            resolvedActor,
            WorkOrigin.Create(channel, resolvedActor, description, url),
            null);
    }
}
