namespace Workable;

public sealed record WorkSystemAccessSummary(
    bool CanConnect,
    bool IsSystemAdministrator,
    bool IsWorkAdministrator,
    bool CanViewDiagnostics,
    bool CanControlSystem,
    bool CanReadAllWork,
    bool CanOperateAllWork,
    int TotalDefinitionCount,
    int ReadableDefinitionCount,
    int OperableDefinitionCount);
