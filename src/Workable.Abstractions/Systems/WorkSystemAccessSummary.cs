namespace Workable;

public sealed record WorkSystemAccessSummary(
    bool IsSystemAdministrator,
    bool IsWorkAdministrator,
    bool CanViewDiagnostics,
    bool CanControlSystem,
    bool CanReadAllWork,
    bool CanOperateAllWork,
    int TotalDefinitionCount,
    int ReadableDefinitionCount,
    int OperableDefinitionCount)
{
    public bool HasAnyAccess()
        => this.IsSystemAdministrator ||
            this.IsWorkAdministrator ||
            this.CanViewDiagnostics ||
            this.CanControlSystem ||
            this.CanReadAllWork ||
            this.CanOperateAllWork ||
            this.ReadableDefinitionCount > 0 ||
            this.OperableDefinitionCount > 0;
}
