namespace Workable;

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
    public bool HasReaderFailure => this.ReaderFailureType is not null;

    public bool HasLeaseRenewalFailure => this.LeaseRenewalFailureType is not null;

    public bool HasCleanupFailure => this.CleanupFailureType is not null;
}
