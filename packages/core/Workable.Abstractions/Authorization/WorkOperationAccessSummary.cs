namespace Workable;

/// <summary>
/// Describes the exact worker and workflow operation categories available to one caller.
/// </summary>
public sealed record WorkOperationAccessSummary(
    bool CanStartWorker,
    bool CanPauseWorker,
    bool CanCancelWorker,
    bool CanPushWorker,
    bool CanPurgeWorker,
    bool CanReconfigureDefinition,
    bool CanStartWorkflow,
    bool CanResumeWorkflow,
    bool CanPauseWorkflow,
    bool CanCancelWorkflow)
{
    /// <summary>
    /// Creates a conservative exact-operation summary from system-wide access only.
    /// Per-definition coarse counts are intentionally not expanded into unrelated operation categories.
    /// </summary>
    public static WorkOperationAccessSummary FromSystemWideAccess(WorkSystemAccessSummary access)
    {
        ArgumentNullException.ThrowIfNull(access);

        var canOperateWork = access.CanOperateAllWork && access.TotalDefinitionCount > 0;
        var canOperateWorkflows = access.CanOperateAllWork && access.OperableWorkflowDefinitionCount > 0;
        return new(
            canOperateWork,
            canOperateWork,
            canOperateWork,
            canOperateWork,
            canOperateWork,
            canOperateWork,
            canOperateWorkflows,
            canOperateWorkflows,
            canOperateWorkflows,
            canOperateWorkflows);
    }
}
