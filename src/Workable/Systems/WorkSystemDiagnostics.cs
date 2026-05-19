namespace Workable;

internal sealed class WorkSystemDiagnostics(
    WorkSystemQueueDiagnosticsTracker queueDiagnostics,
    WorkSystemReadModel readModel,
    WorkerOperations workers) : IWorkSystemDiagnostics
{
    public WorkSystemQueueDiagnostics Queue => queueDiagnostics.Diagnostics;

    public WorkSystemReadModelDiagnostics ReadModel => readModel.Diagnostics;

    public WorkSystemRetentionDiagnostics Retention => workers.RetentionDiagnostics;
}
