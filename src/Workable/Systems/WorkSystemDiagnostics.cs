namespace Workable;

internal sealed class WorkSystemDiagnostics(WorkSystemReadModel readModel) : IWorkSystemDiagnostics
{
    public WorkSystemReadModelDiagnostics ReadModel => readModel.Diagnostics;
}
