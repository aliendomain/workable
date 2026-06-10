namespace Workable;

/// <summary>
/// Describes where a Workable action or worker originated.
/// </summary>
/// <param name="Id">The unique identifier for the origin record.</param>
/// <param name="CreatedAt">The time the origin record was created.</param>
/// <param name="Channel">The invocation channel through which the action entered Workable.</param>
/// <param name="Actor">The actor associated with the origin.</param>
/// <param name="Surface">
/// The surface that presented Workable to the caller, distinguishing built-in Workable adapters from host-defined entry points.
/// </param>
public sealed record WorkOrigin(
    WorkOriginId Id,
    DateTimeOffset CreatedAt,
    WorkInvocationChannel Channel,
    WorkActor Actor,
    WorkOriginSurface Surface = WorkOriginSurface.HostApplication)
{
    /// <summary>
    /// Creates a new origin record for the supplied invocation channel and actor.
    /// </summary>
    /// <param name="channel">The invocation channel through which the action entered Workable.</param>
    /// <param name="actor">The actor associated with the origin, or <see cref="WorkActor.Unknown"/> when omitted.</param>
    /// <param name="surface">
    /// The surface that presented Workable to the caller, distinguishing built-in Workable adapters from host-defined entry points.
    /// </param>
    /// <returns>A new origin record stamped with a generated origin id and the current UTC time.</returns>
    public static WorkOrigin Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null,
        WorkOriginSurface surface = WorkOriginSurface.HostApplication)
        => new(
            WorkOriginId.New(),
            DateTimeOffset.UtcNow,
            channel,
            actor ?? WorkActor.Unknown,
            surface);

    /// <summary>
    /// Returns a copy of the origin with a different surface classification.
    /// </summary>
    /// <param name="surface">The updated surface classification.</param>
    /// <returns>The original origin when the surface is unchanged; otherwise a copy with the new surface.</returns>
    public WorkOrigin WithSurface(WorkOriginSurface surface)
        => this.Surface == surface
            ? this
            : this with { Surface = surface };
}
