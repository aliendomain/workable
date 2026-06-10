namespace Workable;

/// <summary>
/// Represents the effective access a caller has to a system and its registered definitions.
/// </summary>
/// <param name="IsSystemAdministrator">Whether the caller is recognized as a system administrator.</param>
/// <param name="IsWorkAdministrator">Whether the caller is recognized as a work administrator.</param>
/// <param name="CanViewDiagnostics">Whether the caller can view system diagnostics.</param>
/// <param name="CanControlSystem">Whether the caller can start and stop the system lifecycle.</param>
/// <param name="CanReadAllWork">Whether the caller can read every definition in the system.</param>
/// <param name="CanOperateAllWork">Whether the caller can operate every definition in the system.</param>
/// <param name="TotalDefinitionCount">The total number of registered definitions in the system.</param>
/// <param name="ReadableDefinitionCount">The number of definitions the caller can read.</param>
/// <param name="OperableDefinitionCount">The number of definitions the caller can operate.</param>
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
    /// <summary>
    /// Determines whether the caller has any meaningful access to the system.
    /// </summary>
    /// <returns><see langword="true"/> when any system-level or definition-level access is present; otherwise <see langword="false"/>.</returns>
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
