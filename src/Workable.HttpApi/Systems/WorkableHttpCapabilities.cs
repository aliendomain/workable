namespace Workable;
public sealed record WorkableHttpCapabilities(
    WorkRealtimeCapability Realtime,
    bool PersistentCoordinationAvailable);
