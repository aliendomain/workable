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
    /// Gets whether the caller can discover every definition and its schemas.
    /// </summary>
    public bool CanDiscoverAllWork { get; init; }

    /// <summary>
    /// Gets the number of definitions the caller can discover.
    /// </summary>
    public int DiscoverableDefinitionCount { get; init; }

    /// <summary>
    /// Gets the number of workflow definitions the caller can read.
    /// </summary>
    public int ReadableWorkflowDefinitionCount { get; init; }

    /// <summary>
    /// Gets the number of workflow definitions the caller can operate.
    /// </summary>
    public int OperableWorkflowDefinitionCount { get; init; }

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
            this.CanDiscoverAllWork ||
            this.DiscoverableDefinitionCount > 0 ||
            this.ReadableDefinitionCount > 0 ||
            this.OperableDefinitionCount > 0 ||
            this.ReadableWorkflowDefinitionCount > 0 ||
            this.OperableWorkflowDefinitionCount > 0;
}
