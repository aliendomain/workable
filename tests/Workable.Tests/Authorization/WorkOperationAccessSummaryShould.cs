using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkOperationAccessSummaryShould
{
    [Fact]
    public void NotInventExactActionsFromPerDefinitionOperateCounts()
    {
        var coarseAccess = Access(
            canOperateAllWork: false,
            totalDefinitionCount: 2,
            operableDefinitionCount: 1,
            operableWorkflowDefinitionCount: 1);

        var exact = WorkOperationAccessSummary.FromSystemWideAccess(coarseAccess);

        Assert.False(exact.CanStartWorker);
        Assert.False(exact.CanPauseWorker);
        Assert.False(exact.CanCancelWorker);
        Assert.False(exact.CanPushWorker);
        Assert.False(exact.CanPurgeWorker);
        Assert.False(exact.CanReconfigureDefinition);
        Assert.False(exact.CanStartWorkflow);
        Assert.False(exact.CanResumeWorkflow);
        Assert.False(exact.CanPauseWorkflow);
        Assert.False(exact.CanCancelWorkflow);
    }

    [Fact]
    public void ExpandSystemWideOperateAccessOnlyForSurfacesThatExist()
    {
        var workOnly = WorkOperationAccessSummary.FromSystemWideAccess(Access(
            canOperateAllWork: true,
            totalDefinitionCount: 1,
            operableDefinitionCount: 1,
            operableWorkflowDefinitionCount: 0));
        var workflowOnly = WorkOperationAccessSummary.FromSystemWideAccess(Access(
            canOperateAllWork: true,
            totalDefinitionCount: 0,
            operableDefinitionCount: 0,
            operableWorkflowDefinitionCount: 1));

        Assert.True(workOnly.CanStartWorker);
        Assert.True(workOnly.CanPauseWorker);
        Assert.True(workOnly.CanCancelWorker);
        Assert.True(workOnly.CanPushWorker);
        Assert.True(workOnly.CanPurgeWorker);
        Assert.True(workOnly.CanReconfigureDefinition);
        Assert.False(workOnly.CanStartWorkflow);
        Assert.False(workOnly.CanResumeWorkflow);
        Assert.False(workOnly.CanPauseWorkflow);
        Assert.False(workOnly.CanCancelWorkflow);

        Assert.False(workflowOnly.CanStartWorker);
        Assert.False(workflowOnly.CanPauseWorker);
        Assert.False(workflowOnly.CanCancelWorker);
        Assert.False(workflowOnly.CanPushWorker);
        Assert.False(workflowOnly.CanPurgeWorker);
        Assert.False(workflowOnly.CanReconfigureDefinition);
        Assert.True(workflowOnly.CanStartWorkflow);
        Assert.True(workflowOnly.CanResumeWorkflow);
        Assert.True(workflowOnly.CanPauseWorkflow);
        Assert.True(workflowOnly.CanCancelWorkflow);
    }

    [Fact]
    public void RejectNullCoarseAccess()
        => Assert.Throws<ArgumentNullException>(() => WorkOperationAccessSummary.FromSystemWideAccess(null!));

    private static WorkSystemAccessSummary Access(
        bool canOperateAllWork,
        int totalDefinitionCount,
        int operableDefinitionCount,
        int operableWorkflowDefinitionCount)
        => new(
            IsSystemAdministrator: false,
            IsWorkAdministrator: false,
            CanViewDiagnostics: false,
            CanControlSystem: false,
            CanReadAllWork: false,
            CanOperateAllWork: canOperateAllWork,
            TotalDefinitionCount: totalDefinitionCount,
            ReadableDefinitionCount: 0,
            OperableDefinitionCount: operableDefinitionCount)
        {
            OperableWorkflowDefinitionCount = operableWorkflowDefinitionCount,
        };
}
