namespace Workable;

public sealed record WorkableRealtimeEventCriteria(
    IReadOnlyList<string>? EventTypes = null,
    IReadOnlyList<string>? DefinitionNames = null,
    IReadOnlyList<WorkableRealtimeEventKeyCriteria>? Keys = null);

public sealed record WorkableRealtimeEventKeyCriteria(
    WorkKeyKind? Kind,
    string Type,
    string Value);
