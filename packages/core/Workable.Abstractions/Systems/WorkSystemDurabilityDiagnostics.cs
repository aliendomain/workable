namespace Workable;

/// <summary>
/// Describes durable queue materialization, lease renewal, and cleanup health.
/// </summary>
/// <param name="AcceptedWaiterCount">The number of accepted durable queue requests waiting to materialize into workers.</param>
/// <param name="OldestAcceptedWaiterAge">How long the oldest accepted durable request has been waiting to materialize.</param>
/// <param name="PendingCleanupCount">The number of durable cleanup items waiting to be processed.</param>
/// <param name="OldestPendingCleanupAge">How long the oldest durable cleanup item has been waiting.</param>
/// <param name="ReaderFailureType">The exception type from the most recent durable reader failure, when one occurred.</param>
/// <param name="ReaderFailureMessage">The message from the most recent durable reader failure, when one occurred.</param>
/// <param name="ClaimAttemptCount">The number of durable queue claim attempts made by the reader.</param>
/// <param name="ClaimedEntryCount">The total number of durable queue entries returned by claim attempts.</param>
/// <param name="EmptyClaimCount">The number of claim attempts that returned no entries.</param>
/// <param name="LastClaimedEntryCount">The number of entries returned by the most recent claim attempt.</param>
/// <param name="TotalClaimElapsed">The total elapsed time spent claiming durable queue entries.</param>
/// <param name="LastClaimElapsed">The elapsed time spent in the most recent claim attempt.</param>
/// <param name="MaxClaimElapsed">The longest elapsed claim attempt.</param>
/// <param name="TotalClaimAcceptanceElapsed">The total elapsed time spent accepting claimed entries into the in-memory runtime.</param>
/// <param name="LastClaimAcceptanceElapsed">The elapsed time spent accepting entries from the most recent claim attempt.</param>
/// <param name="MaxClaimAcceptanceElapsed">The longest elapsed acceptance phase after a claim attempt.</param>
/// <param name="RecentClaimSamples">Recent detailed claim samples, when detailed claim sampling is enabled.</param>
/// <param name="LeaseRenewalFailureType">The exception type from the most recent lease-renewal failure, when one occurred.</param>
/// <param name="LeaseRenewalFailureMessage">The message from the most recent lease-renewal failure, when one occurred.</param>
/// <param name="CleanupFailureType">The exception type from the most recent cleanup failure, when one occurred.</param>
/// <param name="CleanupFailureMessage">The message from the most recent cleanup failure, when one occurred.</param>
public sealed record WorkSystemDurabilityDiagnostics(
    int AcceptedWaiterCount,
    TimeSpan OldestAcceptedWaiterAge,
    int PendingCleanupCount,
    TimeSpan OldestPendingCleanupAge,
    string? ReaderFailureType,
    string? ReaderFailureMessage,
    long ClaimAttemptCount,
    long ClaimedEntryCount,
    long EmptyClaimCount,
    int LastClaimedEntryCount,
    TimeSpan TotalClaimElapsed,
    TimeSpan LastClaimElapsed,
    TimeSpan MaxClaimElapsed,
    TimeSpan TotalClaimAcceptanceElapsed,
    TimeSpan LastClaimAcceptanceElapsed,
    TimeSpan MaxClaimAcceptanceElapsed,
    IReadOnlyList<WorkQueueDurabilityClaimSample> RecentClaimSamples,
    string? LeaseRenewalFailureType,
    string? LeaseRenewalFailureMessage,
    string? CleanupFailureType,
    string? CleanupFailureMessage)
{
    /// <summary>
    /// Gets a value indicating whether the durable reader loop has recorded an internal failure.
    /// </summary>
    public bool HasReaderFailure => this.ReaderFailureType is not null;

    /// <summary>
    /// Gets the average elapsed time spent in each durable queue claim attempt.
    /// </summary>
    public TimeSpan AverageClaimElapsed => this.ClaimAttemptCount == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(this.TotalClaimElapsed.Ticks / this.ClaimAttemptCount);

    /// <summary>
    /// Gets the average elapsed time spent accepting claimed entries into the in-memory runtime.
    /// </summary>
    public TimeSpan AverageClaimAcceptanceElapsed => this.ClaimAttemptCount == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(this.TotalClaimAcceptanceElapsed.Ticks / this.ClaimAttemptCount);

    /// <summary>
    /// Gets the average number of entries returned by each durable queue claim attempt.
    /// </summary>
    public double AverageClaimedEntries => this.ClaimAttemptCount == 0
        ? 0
        : (double)this.ClaimedEntryCount / this.ClaimAttemptCount;

    /// <summary>
    /// Gets the average claimed-entry throughput for time spent inside durable queue claim attempts.
    /// </summary>
    public double ClaimedEntriesPerSecond => this.TotalClaimElapsed <= TimeSpan.Zero
        ? 0
        : this.ClaimedEntryCount / this.TotalClaimElapsed.TotalSeconds;

    /// <summary>
    /// Gets the average claimed-entry throughput for time spent accepting claimed entries into the in-memory runtime.
    /// </summary>
    public double ClaimAcceptanceEntriesPerSecond => this.TotalClaimAcceptanceElapsed <= TimeSpan.Zero
        ? 0
        : this.ClaimedEntryCount / this.TotalClaimAcceptanceElapsed.TotalSeconds;

    /// <summary>
    /// Gets a value indicating whether the durable lease-renewal loop has recorded an internal failure.
    /// </summary>
    public bool HasLeaseRenewalFailure => this.LeaseRenewalFailureType is not null;

    /// <summary>
    /// Gets a value indicating whether the durable cleanup loop has recorded an internal failure.
    /// </summary>
    public bool HasCleanupFailure => this.CleanupFailureType is not null;
}

/// <summary>
/// Describes one recent durable queue claim attempt when detailed claim sampling is enabled.
/// </summary>
/// <param name="Sequence">The monotonic sample sequence.</param>
/// <param name="StartedAt">The UTC time when the claim attempt started.</param>
/// <param name="CompletedAt">The UTC time when claim acceptance completed.</param>
/// <param name="ClaimedEntryCount">The number of entries returned by the claim attempt.</param>
/// <param name="ClaimElapsed">The elapsed time spent claiming entries from the persistence store.</param>
/// <param name="AcceptanceElapsed">The elapsed time spent accepting claimed entries into memory.</param>
public readonly record struct WorkQueueDurabilityClaimSample(
    long Sequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ClaimedEntryCount,
    TimeSpan ClaimElapsed,
    TimeSpan AcceptanceElapsed)
{
    /// <summary>
    /// Gets the claimed-entry throughput for this claim attempt.
    /// </summary>
    public double ClaimedEntriesPerSecond => this.ClaimElapsed <= TimeSpan.Zero
        ? 0
        : this.ClaimedEntryCount / this.ClaimElapsed.TotalSeconds;
}
