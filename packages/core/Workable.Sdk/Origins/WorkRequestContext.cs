namespace Workable;

/// <summary>
/// Carries caller identity, origin, and authorization context into Workable system operations.
/// </summary>
/// <param name="Origin">The origin metadata to record for the caller and invocation channel.</param>
/// <param name="Description">Optional human-readable context describing why the caller is performing the action.</param>
/// <param name="Url">Optional URL that points back to the caller's originating page or resource.</param>
/// <param name="Authorization">
/// An optional precomputed authorization snapshot that Workable can use instead of resolving groups on demand when it is scoped to the target system.
/// </param>
/// <param name="IsAuthenticated">
/// Indicates whether the caller should count as authenticated for rules that distinguish authenticated known actors.
/// </param>
public sealed record WorkRequestContext(
    WorkOrigin Origin,
    string? Description = null,
    string? Url = null,
    WorkAuthorizationSnapshot? Authorization = null,
    bool IsAuthenticated = false)
{
    /// <summary>
    /// Gets the actor associated with the request origin.
    /// </summary>
    public WorkActor Actor => this.Origin.Actor;

    /// <summary>
    /// Gets the invocation channel associated with the request origin.
    /// </summary>
    public WorkInvocationChannel Channel => this.Origin.Channel;

    /// <summary>
    /// Gets the time the request origin was created.
    /// </summary>
    public DateTimeOffset CreatedAt => this.Origin.CreatedAt;

    /// <summary>
    /// Gets the surface that presented Workable to the caller.
    /// </summary>
    public WorkOriginSurface Surface => this.Origin.Surface;

    /// <summary>
    /// Creates a request context from an existing origin record.
    /// </summary>
    /// <param name="origin">The origin metadata to carry into Workable.</param>
    public WorkRequestContext(WorkOrigin origin)
        : this(origin ?? throw new ArgumentNullException(nameof(origin)), null, null, null, false)
    {
    }

    /// <summary>
    /// Creates a request context for the supplied invocation channel and optional actor.
    /// </summary>
    /// <param name="channel">The invocation channel through which the caller is entering Workable.</param>
    /// <param name="actor">The caller identity to record, or <see cref="WorkActor.Unknown"/> when omitted.</param>
    /// <param name="description">Optional human-readable context describing why the caller is performing the action.</param>
    /// <param name="url">Optional URL that points back to the caller's originating page or resource.</param>
    /// <param name="isAuthenticated">
    /// Whether the caller should count as authenticated for rules such as
    /// <c>AllowOperateToKnownAuthenticatedUsers()</c>,
    /// <c>AllowQueueToKnownAuthenticatedUsers()</c>, or
    /// <c>AllowOperationsToKnownAuthenticatedUsers(...)</c>.
    /// </param>
    /// <param name="surface">
    /// The surface that presented Workable to the caller, distinguishing built-in Workable adapters from host-defined entry points.
    /// </param>
    /// <returns>A new request context with a generated origin record.</returns>
    public static WorkRequestContext Create(
        WorkInvocationChannel channel,
        WorkActor? actor = null,
        string? description = null,
        string? url = null,
        bool isAuthenticated = false,
        WorkOriginSurface surface = WorkOriginSurface.HostApplication)
    {
        var resolvedActor = actor ?? WorkActor.Unknown;
        return new WorkRequestContext(
            WorkOrigin.Create(channel, resolvedActor, surface),
            description,
            url,
            null,
            isAuthenticated);
    }

    /// <summary>
    /// Returns a copy of the request context with any precomputed authorization snapshot removed.
    /// </summary>
    /// <returns>The original context when no authorization snapshot is present; otherwise a copy without it.</returns>
    public WorkRequestContext WithoutAuthorization()
        => this.Authorization is null
            ? this
            : this with { Authorization = null };

    /// <summary>
    /// Returns a copy of the request context with a different origin-surface classification.
    /// </summary>
    /// <param name="surface">The updated surface classification.</param>
    /// <returns>The original request context when the surface is unchanged; otherwise a copy with the new surface.</returns>
    public WorkRequestContext WithSurface(WorkOriginSurface surface)
        => this.Surface == surface
            ? this
            : this with { Origin = this.Origin.WithSurface(surface) };
}
