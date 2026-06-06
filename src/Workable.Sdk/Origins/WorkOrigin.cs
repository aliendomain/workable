namespace Workable;

public sealed record WorkOrigin(
    WorkOriginId Id,
    DateTimeOffset CreatedAt,
    WorkInvocationChannel Channel,
    WorkActor Actor,
    IReadOnlySet<WorkIdentifier>? Identifiers = null)
{
    public static WorkOrigin Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null,
        IEnumerable<WorkIdentifier>? identifiers = null)
        => new(
            WorkOriginId.New(),
            DateTimeOffset.UtcNow,
            channel,
            actor ?? WorkActor.Unknown,
            identifiers is null ? null : identifiers.ToHashSet());
}
