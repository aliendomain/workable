namespace Workable;

public sealed record WorkOrigin(
    WorkOriginId Id,
    DateTimeOffset CreatedAt,
    WorkInvocationChannel Channel,
    WorkActor Actor,
    string? Description = null,
    string? Url = null,
    IReadOnlySet<WorkIdentifier>? Identifiers = null,
    WorkOrigin? Parent = null)
{
    public static WorkOrigin Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null,
        string? description = null,
        string? url = null,
        IEnumerable<WorkIdentifier>? identifiers = null,
        WorkOrigin? parent = null)
        => new(
            WorkOriginId.New(),
            DateTimeOffset.UtcNow,
            channel,
            actor ?? WorkActor.Unknown,
            description,
            url,
            identifiers is null ? null : identifiers.ToHashSet(),
            parent);
}
