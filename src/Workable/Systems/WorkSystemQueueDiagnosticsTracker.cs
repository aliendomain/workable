namespace Workable;

internal sealed class WorkSystemQueueDiagnosticsTracker
{
    private static readonly HashSet<string> AlertableRejectedCodes = new(StringComparer.Ordinal)
    {
        "workable.system.capacity_reached",
        "workable.system.not_started",
        "workable.system.stopping",
        "workable.queue_durability.store_required",
        "workable.queue_durability.store_unreachable",
        "workable.idempotency.persistence_store_required",
        "workable.idempotency.persistence_store_unreachable",
    };

    private readonly Lock sync = new();
    private long rejectedWorkCount;
    private DateTimeOffset? lastRejectedAt;
    private WorkQueueStatus? lastRejectedStatus;
    private WorkDefinitionId? lastRejectedDefinitionId;
    private string? lastRejectedCode;
    private string? lastRejectedMessage;
    private long alertableRejectedWorkCount;
    private string? lastAlertableRejectedCode;
    private string? lastAlertableRejectedMessage;

    public WorkSystemQueueDiagnostics Diagnostics
    {
        get
        {
            lock (this.sync)
            {
                return new WorkSystemQueueDiagnostics(
                    this.rejectedWorkCount,
                    this.lastRejectedAt,
                    this.lastRejectedStatus,
                    this.lastRejectedDefinitionId,
                    this.lastRejectedCode,
                    this.lastRejectedMessage,
                    this.alertableRejectedWorkCount,
                    this.lastAlertableRejectedCode,
                    this.lastAlertableRejectedMessage);
            }
        }
    }

    public void RecordRejected(WorkQueueOutcome outcome)
    {
        var primaryMessage = outcome.Messages.FirstOrDefault(message => message.Severity.IsError()) ??
            outcome.Messages.FirstOrDefault();
        var primaryCode = primaryMessage?.Code;
        var primaryText = primaryMessage?.Text;

        lock (this.sync)
        {
            this.rejectedWorkCount++;
            this.lastRejectedAt = DateTimeOffset.UtcNow;
            this.lastRejectedStatus = outcome.Status;
            this.lastRejectedDefinitionId = outcome.DefinitionId;
            this.lastRejectedCode = primaryCode;
            this.lastRejectedMessage = primaryText;
            if (IsAlertableRejectionCode(primaryCode))
            {
                this.alertableRejectedWorkCount++;
                this.lastAlertableRejectedCode = primaryCode;
                this.lastAlertableRejectedMessage = primaryText;
            }
        }
    }

    internal static bool IsAlertableRejectionCode(string? code)
        => code is not null && AlertableRejectedCodes.Contains(code);
}
