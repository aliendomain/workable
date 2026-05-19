namespace Workable;

public sealed record WorkSystemConcurrencyDiagnostics(
    int DeferredStartCount,
    TimeSpan OldestDeferredStartAge,
    int LastDrainReleasedCount);
