namespace Workable;

internal sealed class WorkSystemIdempotencyDiagnosticsTracker
{
    private readonly Lock sync = new();
    private long duplicateRejectionCount;
    private WorkIdempotencyStorage? lastDuplicateRejectedStorage;

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
        WorkIdempotencyStorage storage)
    {
        lock (this.sync)
        {
            this.duplicateRejectionCount++;
            this.lastDuplicateRejectedStorage = storage;
        }
    }
}
