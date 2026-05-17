namespace Workable;

internal sealed class WorkSystemDiagnostics(
    WorkSystemReadModel readModel,
    WorkerOperations workers) : IWorkSystemDiagnostics
{
    public WorkSystemReadModelDiagnostics ReadModel => readModel.Diagnostics;

    public WorkSystemRetentionDiagnostics Retention => workers.RetentionDiagnostics;
}
