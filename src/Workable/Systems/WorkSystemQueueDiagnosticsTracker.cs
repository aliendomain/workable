namespace Workable;

internal sealed class WorkSystemQueueDiagnosticsTracker
{
    private readonly Lock sync = new();
    private long rejectedWorkCount;
    private DateTimeOffset? lastRejectedAt;
    private WorkQueueStatus? lastRejectedStatus;
    private WorkDefinitionId? lastRejectedDefinitionId;
    private string? lastRejectedCode;
    private string? lastRejectedMessage;

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
                    this.lastRejectedMessage);
            }
        }
    }

    public void RecordRejected(WorkQueueOutcome outcome)
    {
        var primaryMessage = outcome.Messages.FirstOrDefault(message => message.Severity is WorkMessageSeverity.Error) ??
            outcome.Messages.FirstOrDefault();

        lock (this.sync)
        {
            this.rejectedWorkCount++;
            this.lastRejectedAt = DateTimeOffset.UtcNow;
            this.lastRejectedStatus = outcome.Status;
            this.lastRejectedDefinitionId = outcome.DefinitionId;
            this.lastRejectedCode = primaryMessage?.Code;
            this.lastRejectedMessage = primaryMessage?.Text;
        }
    }
}
