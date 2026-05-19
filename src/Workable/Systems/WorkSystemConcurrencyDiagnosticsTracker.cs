using System.Collections.Generic;

namespace Workable;

internal sealed class WorkSystemConcurrencyDiagnosticsTracker
{
    private int lastDrainReleasedCount;

    public WorkSystemConcurrencyDiagnostics Snapshot(IReadOnlyList<WorkDefinitionConcurrencyDiagnosticsSnapshot> snapshots)
    {
        var deferredStartCount = 0;
        var oldestDeferredStartAge = TimeSpan.Zero;
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots)
        {
            deferredStartCount += snapshot.DeferredStartCount;
            if (snapshot.OldestDeferredStartAt is { } candidate)
            {
                var age = candidate < now ? now - candidate : TimeSpan.Zero;
                if (age > oldestDeferredStartAge)
                {
                    oldestDeferredStartAge = age;
                }
            }
        }

        return new WorkSystemConcurrencyDiagnostics(
            deferredStartCount,
            oldestDeferredStartAge,
            Volatile.Read(ref this.lastDrainReleasedCount));
    }

    public void RecordDrain(int releasedCount)
        => Volatile.Write(ref this.lastDrainReleasedCount, releasedCount);

    public void Clear()
        => Volatile.Write(ref this.lastDrainReleasedCount, 0);
}

internal readonly record struct WorkDefinitionConcurrencyDiagnosticsSnapshot(
    int DeferredStartCount,
    DateTimeOffset? OldestDeferredStartAt);
