using Microsoft.AspNetCore.Http;
using Workable;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpRouteResultsShould
{
    [Fact]
    public void MapQueueOutcomesToStableHttpStatuses()
    {
        AssertStatus(StatusCodes.Status200OK, WorkableHttpRouteResults.ToQueueHttpResult(QueueResult(
            WorkableHttpWorkStatus.Accepted,
            WorkQueueStatus.Accepted)));
        AssertStatus(StatusCodes.Status404NotFound, WorkableHttpRouteResults.ToQueueHttpResult(QueueResult(
            WorkableHttpWorkStatus.Rejected,
            WorkQueueStatus.NotFound)));
        AssertStatus(StatusCodes.Status403Forbidden, WorkableHttpRouteResults.ToQueueHttpResult(QueueResult(
            WorkableHttpWorkStatus.Rejected,
            WorkQueueStatus.Unauthorized)));
        AssertStatus(StatusCodes.Status400BadRequest, WorkableHttpRouteResults.ToQueueHttpResult(QueueResult(
            WorkableHttpWorkStatus.Rejected,
            WorkQueueStatus.Invalid)));
    }

    [Fact]
    public void MapWorkerActionOutcomesToStableHttpStatuses()
    {
        AssertStatus(StatusCodes.Status200OK, Action(WorkActionStatus.Accepted));
        AssertStatus(StatusCodes.Status404NotFound, Action(WorkActionStatus.NotFound));
        AssertStatus(StatusCodes.Status403Forbidden, Action(WorkActionStatus.Unauthorized));
        AssertStatus(StatusCodes.Status409Conflict, Action(WorkActionStatus.Conflict));
        AssertStatus(StatusCodes.Status400BadRequest, Action(WorkActionStatus.Invalid));

        static IResult Action(WorkActionStatus status)
            => WorkableHttpRouteResults.ToActionHttpResult(
                new WorkActionOutcome(status, WorkAction.Start, null, null, []));
    }

    [Fact]
    public void MapWorkflowOutcomesToStableHttpStatuses()
    {
        AssertStatus(StatusCodes.Status200OK, Start(WorkableHttpWorkflowStartStatus.Accepted));
        AssertStatus(StatusCodes.Status404NotFound, Start(WorkableHttpWorkflowStartStatus.NotFound));
        AssertStatus(StatusCodes.Status403Forbidden, Start(WorkableHttpWorkflowStartStatus.Unauthorized));
        AssertStatus(StatusCodes.Status400BadRequest, Start(WorkableHttpWorkflowStartStatus.Invalid));

        AssertStatus(StatusCodes.Status200OK, Action(WorkableHttpWorkflowActionStatus.Accepted));
        AssertStatus(StatusCodes.Status404NotFound, Action(WorkableHttpWorkflowActionStatus.NotFound));
        AssertStatus(StatusCodes.Status403Forbidden, Action(WorkableHttpWorkflowActionStatus.Unauthorized));
        AssertStatus(StatusCodes.Status400BadRequest, Action(WorkableHttpWorkflowActionStatus.Invalid));

        static IResult Start(WorkableHttpWorkflowStartStatus status)
            => WorkableHttpRouteResults.ToWorkflowStartHttpResult(new(status, null, null, []));

        static IResult Action(WorkableHttpWorkflowActionStatus status)
            => WorkableHttpRouteResults.ToWorkflowActionHttpResult(new(
                status,
                WorkableHttpWorkflowActionKind.Start,
                Guid.NewGuid(),
                null,
                []));
    }

    [Fact]
    public void MapDefinitionReconfigurationOutcomesToStableHttpStatuses()
    {
        AssertStatus(StatusCodes.Status200OK, Reconfigure(WorkDefinitionReconfigurationStatus.Accepted));
        AssertStatus(StatusCodes.Status404NotFound, Reconfigure(WorkDefinitionReconfigurationStatus.NotFound));
        AssertStatus(StatusCodes.Status403Forbidden, Reconfigure(WorkDefinitionReconfigurationStatus.Unauthorized));
        AssertStatus(StatusCodes.Status409Conflict, Reconfigure(WorkDefinitionReconfigurationStatus.Conflict));
        AssertStatus(StatusCodes.Status400BadRequest, Reconfigure(WorkDefinitionReconfigurationStatus.Invalid));

        static IResult Reconfigure(WorkDefinitionReconfigurationStatus status)
            => WorkableHttpRouteResults.ToDefinitionReconfigurationHttpResult(new(status, null, []));
    }

    [Fact]
    public void MapEverySystemPermissionDenialAndGuardNullExceptions()
    {
        foreach (var permission in new[]
        {
            WorkSystemPermission.AccessSystem,
            WorkSystemPermission.ViewDiagnostics,
            WorkSystemPermission.ControlSystem,
            (WorkSystemPermission)int.MaxValue,
        })
        {
            AssertStatus(
                StatusCodes.Status403Forbidden,
                WorkableHttpRouteResults.AuthorizationDenied(new WorkSystemAccessDeniedException(
                    permission,
                    WorkSystemId.New(),
                    "test-system")));
        }

        Assert.Throws<ArgumentNullException>(() => WorkableHttpRouteResults.AuthorizationDenied(null!));
    }

    private static WorkableHttpWorkResult QueueResult(
        WorkableHttpWorkStatus status,
        WorkQueueStatus queueStatus)
        => new(
            status,
            new WorkQueueOutcome(queueStatus, null, []),
            null,
            null,
            null,
            []);

    private static void AssertStatus(int expected, IResult result)
        => Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
}
