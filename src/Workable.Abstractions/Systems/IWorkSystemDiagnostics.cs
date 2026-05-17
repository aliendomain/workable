namespace Workable;

public interface IWorkSystemDiagnostics
{
    WorkSystemReadModelDiagnostics ReadModel { get; }

    WorkSystemRetentionDiagnostics Retention { get; }
}
