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
    /// Gets a value indicating whether the durable lease-renewal loop has recorded an internal failure.
    /// </summary>
    public bool HasLeaseRenewalFailure => this.LeaseRenewalFailureType is not null;

    /// <summary>
    /// Gets a value indicating whether the durable cleanup loop has recorded an internal failure.
    /// </summary>
    public bool HasCleanupFailure => this.CleanupFailureType is not null;
}
