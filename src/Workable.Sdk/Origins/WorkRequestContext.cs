namespace Workable;

public sealed record WorkRequestContext(
    WorkOrigin Origin,
    string? Description = null,
    string? Url = null,
    WorkAuthorizationSnapshot? Authorization = null,
    bool IsAuthenticated = false)
{
    public WorkActor Actor => this.Origin.Actor;

    public WorkInvocationChannel Channel => this.Origin.Channel;

    public DateTimeOffset CreatedAt => this.Origin.CreatedAt;

    public WorkRequestContext(WorkOrigin origin)
        : this(origin ?? throw new ArgumentNullException(nameof(origin)), null, null, null, false)
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
            WorkOrigin.Create(channel, resolvedActor),
            description,
            url,
            null,
            isAuthenticated);
    }

    public WorkRequestContext WithoutAuthorization()
        => this.Authorization is null
            ? this
            : this with { Authorization = null };
}
