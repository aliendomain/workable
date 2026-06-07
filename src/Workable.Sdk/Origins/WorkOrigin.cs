namespace Workable;

public sealed record WorkOrigin(
    WorkOriginId Id,
    DateTimeOffset CreatedAt,
    WorkInvocationChannel Channel,
    WorkActor Actor)
{
    public static WorkOrigin Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null)
        => new(
            WorkOriginId.New(),
            DateTimeOffset.UtcNow,
            channel,
            actor ?? WorkActor.Unknown);
}
