namespace Workable;

public sealed record WorkRequestContext(
    WorkActor Actor,
    WorkOrigin Origin,
    WorkAuthorizationSnapshot? Authorization = null,
    bool IsAuthenticated = false)
{
    public WorkRequestContext(WorkOrigin origin)
        : this(origin?.Actor ?? throw new ArgumentNullException(nameof(origin)), origin, null, false)
    {
    }

    public static WorkRequestContext Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null,
        string? description = null,
        string? url = null,
        bool isAuthenticated = false)
    {
        var resolvedActor = actor ?? WorkActor.Unknown;
        return new WorkRequestContext(
            resolvedActor,
            WorkOrigin.Create(channel, resolvedActor, description, url),
            null,
            isAuthenticated);
    }
}
