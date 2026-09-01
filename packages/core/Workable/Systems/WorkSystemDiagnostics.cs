namespace Workable;

internal sealed class WorkSystemDiagnostics(
    WorkSystemQueueDiagnosticsTracker queueDiagnostics,
    WorkSystemReadModel readModel,
    WorkerOperations workers,
    Func<WorkSystemExecutionDiagnosticsPersistenceDiagnostics> getExecutionDiagnosticsPersistence) : IWorkSystemDiagnostics
{
    public WorkSystemExecutionDiagnosticsPersistenceDiagnostics ExecutionDiagnosticsPersistence
        => getExecutionDiagnosticsPersistence();

    public WorkSystemQueueDiagnostics Queue => queueDiagnostics.Diagnostics;

    public WorkSystemReadModelDiagnostics ReadModel => readModel.Diagnostics;

    public WorkSystemRetentionDiagnostics Retention => workers.RetentionDiagnostics;

    public WorkSystemConcurrencyDiagnostics Concurrency => workers.ConcurrencyDiagnostics;

    public WorkSystemDurabilityDiagnostics Durability => workers.DurabilityDiagnostics;

    public WorkSystemIdempotencyDiagnostics Idempotency => workers.IdempotencyDiagnostics;
}
