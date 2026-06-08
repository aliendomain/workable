namespace Workable;

/// <summary>
/// Describes system-specific capabilities advertised through HTTP discovery.
/// </summary>
/// <param name="PersistentCoordinationAvailable">
/// Whether the system currently has persistent coordination available for features such as durable queueing and persistence-backed idempotency.
/// </param>
public sealed record WorkableHttpSystemCapabilities(
    bool PersistentCoordinationAvailable);
