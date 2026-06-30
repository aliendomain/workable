using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Workable;

internal sealed class WorkSystemDurabilityDiagnosticsTracker
{
    private readonly ConcurrentDictionary<WorkerId, long> pendingCleanupQueuedAtUnixTimeMilliseconds = [];
    private readonly object recentClaimSampleSync = new();
    private readonly WorkQueueDurabilityClaimSample[]? recentClaimSamples;
    private LoopFailureState readerFailure = LoopFailureState.None;
    private LoopFailureState leaseRenewalFailure = LoopFailureState.None;
    private LoopFailureState cleanupFailure = LoopFailureState.None;
    private long recentClaimSampleSequence;
    private long claimAttemptCount;
    private long claimedEntryCount;
    private long emptyClaimCount;
    private long totalClaimElapsedTicks;
    private long lastClaimElapsedTicks;
    private long maxClaimElapsedTicks;
    private long totalClaimAcceptanceElapsedTicks;
    private long lastClaimAcceptanceElapsedTicks;
    private long maxClaimAcceptanceElapsedTicks;
    private int lastClaimedEntryCount;
    private int recentClaimSampleCount;

    public WorkSystemDurabilityDiagnosticsTracker(int recentClaimSampleCapacity = 0)
    {
        if (recentClaimSampleCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recentClaimSampleCapacity),
                recentClaimSampleCapacity,
                "Recent claim sample capacity must not be negative.");
        }

        this.recentClaimSamples = recentClaimSampleCapacity == 0
            ? null
            : new WorkQueueDurabilityClaimSample[recentClaimSampleCapacity];
    }

    public WorkSystemDurabilityDiagnostics Snapshot(
        int acceptedWaiterCount,
        TimeSpan oldestAcceptedWaiterAge)
    {
        var now = DateTimeOffset.UtcNow;
        var pendingCleanup = this.GetPendingCleanupSnapshot(now);
        var readerFailure = Volatile.Read(ref this.readerFailure);
        var leaseRenewalFailure = Volatile.Read(ref this.leaseRenewalFailure);
        var cleanupFailure = Volatile.Read(ref this.cleanupFailure);
        var totalClaimElapsedTicks = Interlocked.Read(ref this.totalClaimElapsedTicks);
        var lastClaimElapsedTicks = Interlocked.Read(ref this.lastClaimElapsedTicks);
        var maxClaimElapsedTicks = Interlocked.Read(ref this.maxClaimElapsedTicks);
        var totalClaimAcceptanceElapsedTicks = Interlocked.Read(ref this.totalClaimAcceptanceElapsedTicks);
        var lastClaimAcceptanceElapsedTicks = Interlocked.Read(ref this.lastClaimAcceptanceElapsedTicks);
        var maxClaimAcceptanceElapsedTicks = Interlocked.Read(ref this.maxClaimAcceptanceElapsedTicks);

        return new WorkSystemDurabilityDiagnostics(
            acceptedWaiterCount,
            oldestAcceptedWaiterAge,
            pendingCleanup.Count,
            pendingCleanup.OldestAge,
            readerFailure.Type,
            readerFailure.Message,
            Interlocked.Read(ref this.claimAttemptCount),
            Interlocked.Read(ref this.claimedEntryCount),
            Interlocked.Read(ref this.emptyClaimCount),
            Volatile.Read(ref this.lastClaimedEntryCount),
            TimeSpan.FromTicks(totalClaimElapsedTicks),
            TimeSpan.FromTicks(lastClaimElapsedTicks),
            TimeSpan.FromTicks(maxClaimElapsedTicks),
            TimeSpan.FromTicks(totalClaimAcceptanceElapsedTicks),
            TimeSpan.FromTicks(lastClaimAcceptanceElapsedTicks),
            TimeSpan.FromTicks(maxClaimAcceptanceElapsedTicks),
            this.GetRecentClaimSamplesSnapshot(),
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

    public void RecordClaim(
        int claimedEntryCount,
        TimeSpan elapsed,
        TimeSpan acceptanceElapsed = default,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null)
    {
        Interlocked.Increment(ref this.claimAttemptCount);
        Interlocked.Add(ref this.claimedEntryCount, claimedEntryCount);
        if (claimedEntryCount == 0)
        {
            Interlocked.Increment(ref this.emptyClaimCount);
        }

        Volatile.Write(ref this.lastClaimedEntryCount, claimedEntryCount);
        var elapsedTicks = elapsed.Ticks;
        Interlocked.Exchange(ref this.lastClaimElapsedTicks, elapsedTicks);
        Interlocked.Add(ref this.totalClaimElapsedTicks, elapsedTicks);
        RecordMax(ref this.maxClaimElapsedTicks, elapsedTicks);

        var acceptanceElapsedTicks = acceptanceElapsed.Ticks;
        Interlocked.Exchange(ref this.lastClaimAcceptanceElapsedTicks, acceptanceElapsedTicks);
        Interlocked.Add(ref this.totalClaimAcceptanceElapsedTicks, acceptanceElapsedTicks);
        RecordMax(ref this.maxClaimAcceptanceElapsedTicks, acceptanceElapsedTicks);

        if (this.recentClaimSamples is not null &&
            startedAt is { } sampleStartedAt &&
            completedAt is { } sampleCompletedAt)
        {
            this.RecordRecentClaimSample(
                claimedEntryCount,
                elapsed,
                acceptanceElapsed,
                sampleStartedAt,
                sampleCompletedAt);
        }
    }

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

    private IReadOnlyList<WorkQueueDurabilityClaimSample> GetRecentClaimSamplesSnapshot()
    {
        var samples = this.recentClaimSamples;
        if (samples is null)
        {
            return [];
        }

        lock (this.recentClaimSampleSync)
        {
            var count = this.recentClaimSampleCount;
            if (count == 0)
            {
                return [];
            }

            var snapshot = new WorkQueueDurabilityClaimSample[count];
            var firstSequence = this.recentClaimSampleSequence - count + 1;
            for (var index = 0; index < count; index++)
            {
                var sequence = firstSequence + index;
                snapshot[index] = samples[(int)((sequence - 1) % samples.Length)];
            }

            return snapshot;
        }
    }

    private void RecordRecentClaimSample(
        int sampleClaimedEntryCount,
        TimeSpan elapsed,
        TimeSpan acceptanceElapsed,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var samples = this.recentClaimSamples;
        if (samples is null)
        {
            return;
        }

        lock (this.recentClaimSampleSync)
        {
            var sequence = ++this.recentClaimSampleSequence;
            samples[(int)((sequence - 1) % samples.Length)] = new WorkQueueDurabilityClaimSample(
                sequence,
                startedAt,
                completedAt,
                sampleClaimedEntryCount,
                elapsed,
                acceptanceElapsed);
            if (this.recentClaimSampleCount < samples.Length)
            {
                this.recentClaimSampleCount++;
            }
        }
    }

    private static void RecordMax(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
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
