using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Workable;

internal sealed class WorkSystemDurabilityDiagnosticsTracker
{
    private readonly ConcurrentDictionary<WorkerId, long> pendingCleanupQueuedAtUnixTimeMilliseconds = [];
    private LoopFailureState readerFailure = LoopFailureState.None;
    private LoopFailureState leaseRenewalFailure = LoopFailureState.None;
    private LoopFailureState cleanupFailure = LoopFailureState.None;

    public WorkSystemDurabilityDiagnostics Snapshot(
        int acceptedWaiterCount,
        TimeSpan oldestAcceptedWaiterAge)
    {
        var now = DateTimeOffset.UtcNow;
        var pendingCleanup = this.GetPendingCleanupSnapshot(now);
        var readerFailure = Volatile.Read(ref this.readerFailure);
        var leaseRenewalFailure = Volatile.Read(ref this.leaseRenewalFailure);
        var cleanupFailure = Volatile.Read(ref this.cleanupFailure);

        return new WorkSystemDurabilityDiagnostics(
            acceptedWaiterCount,
            oldestAcceptedWaiterAge,
            pendingCleanup.Count,
            pendingCleanup.OldestAge,
            readerFailure.Type,
            readerFailure.Message,
            leaseRenewalFailure.Type,
            leaseRenewalFailure.Message,
            cleanupFailure.Type,
            cleanupFailure.Message);
    }

    public void TrackCleanupQueued(WorkerId workerId)
        => this.pendingCleanupQueuedAtUnixTimeMilliseconds.TryAdd(
            workerId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public void TrackCleanupCompleted(WorkerId workerId)
        => this.pendingCleanupQueuedAtUnixTimeMilliseconds.TryRemove(workerId, out _);

    public void TrackCleanupCompleted(IEnumerable<WorkerId> workerIds)
    {
        foreach (var workerId in workerIds)
        {
            this.pendingCleanupQueuedAtUnixTimeMilliseconds.TryRemove(workerId, out _);
        }
    }

    public void RecordReaderSuccess()
        => Volatile.Write(ref this.readerFailure, LoopFailureState.None);

    public void RecordReaderFailure(Exception exception)
        => Volatile.Write(ref this.readerFailure, LoopFailureState.FromException(exception));

    public void RecordLeaseRenewalSuccess()
    {
        Volatile.Write(ref this.leaseRenewalFailure, LoopFailureState.None);
    }

    public void RecordLeaseRenewalFailure(Exception exception)
        => Volatile.Write(ref this.leaseRenewalFailure, LoopFailureState.FromException(exception));

    public void RecordCleanupSuccess()
    {
        Volatile.Write(ref this.cleanupFailure, LoopFailureState.None);
    }

    public void RecordCleanupFailure(Exception exception)
        => Volatile.Write(ref this.cleanupFailure, LoopFailureState.FromException(exception));

    private PendingCleanupSnapshot GetPendingCleanupSnapshot(DateTimeOffset now)
    {
        var queuedAtSnapshot = this.pendingCleanupQueuedAtUnixTimeMilliseconds.Values.ToArray();
        if (queuedAtSnapshot.Length == 0)
        {
            return PendingCleanupSnapshot.Empty;
        }

        var oldestAt = DateTimeOffset.FromUnixTimeMilliseconds(queuedAtSnapshot.Min());
        return new PendingCleanupSnapshot(
            queuedAtSnapshot.Length,
            oldestAt < now ? now - oldestAt : TimeSpan.Zero);
    }

    private sealed record LoopFailureState(string? Type, string? Message)
    {
        public static LoopFailureState None { get; } = new(null, null);

        public static LoopFailureState FromException(Exception exception)
            => new(exception.GetType().FullName, exception.Message);
    }

    private readonly record struct PendingCleanupSnapshot(
        int Count,
        TimeSpan OldestAge)
    {
        public static PendingCleanupSnapshot Empty { get; } = new(0, TimeSpan.Zero);
    }
}
