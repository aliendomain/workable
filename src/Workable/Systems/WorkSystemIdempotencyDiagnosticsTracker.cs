namespace Workable;

internal sealed class WorkSystemIdempotencyDiagnosticsTracker
{
    private readonly Lock sync = new();
    private long duplicateRejectionCount;
    private WorkCoordinationStorage? lastDuplicateRejectedStorage;

    public WorkSystemIdempotencyDiagnostics Diagnostics()
    {
        lock (this.sync)
        {
            return new WorkSystemIdempotencyDiagnostics(
                this.duplicateRejectionCount,
                this.lastDuplicateRejectedStorage);
        }
    }

    public void RecordDuplicateRejected(
        WorkDefinitionId definitionId,
        WorkSubjectId subjectId,
        WorkCoordinationStorage storage)
    {
        lock (this.sync)
        {
            this.duplicateRejectionCount++;
            this.lastDuplicateRejectedStorage = storage;
        }
    }
}
