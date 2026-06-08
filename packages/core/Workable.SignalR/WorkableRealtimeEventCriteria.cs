namespace Workable;

/// <summary>
/// Describes the optional filters applied to a raw realtime event subscription.
/// </summary>
/// <param name="EventTypes">The event type names to include.</param>
/// <param name="DefinitionNames">The work definition names to include.</param>
/// <param name="Keys">The work keys that must match for an event to be delivered.</param>
public sealed record WorkableRealtimeEventCriteria(
    IReadOnlyList<string>? EventTypes = null,
    IReadOnlyList<string>? DefinitionNames = null,
    IReadOnlyList<WorkableRealtimeEventKeyCriteria>? Keys = null);

/// <summary>
/// Filters raw realtime events by one structured work key.
/// </summary>
/// <param name="Kind">The optional key kind to match. <see langword="null"/> matches any kind for the same type and value.</param>
/// <param name="Type">The key type to match.</param>
/// <param name="Value">The key value to match.</param>
public sealed record WorkableRealtimeEventKeyCriteria(
    WorkKeyKind? Kind,
    string Type,
    string Value);
