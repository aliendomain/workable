using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeWorkflowViewsShould
{
    private static readonly WorkAuthorizationSnapshot Authorization = WorkAuthorizationSnapshot.CreateForSystem(
        systemName: null,
        new WorkActor("workflow-viewer", "Workflow Viewer", "viewer@example.test"),
        ["workflow.readers"],
        [],
        isAuthenticated: true);

    [Theory]
    [InlineData("workflow-runs", true)]
    [InlineData("WORKFLOW-RUN", true)]
    [InlineData("overview", false)]
    public void RecognizeOnlyWorkflowViewNames(string viewName, bool expected)
        => Assert.Equal(expected, WorkableRealtimeWorkflowViews.IsWorkflowView(viewName));

    [Fact]
    public void PreserveSnapshotAuthenticationStateInSignalRAdapterRequestContexts()
    {
        var authenticated = WorkableRealtimeWorkflowViews.CreateRequestContext(Authorization);
        var unauthenticatedAuthorization = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            Authorization.Actor,
            Authorization.Groups,
            readableDefinitionIds: []);
        var unauthenticated = WorkableRealtimeWorkflowViews.CreateRequestContext(unauthenticatedAuthorization);

        Assert.Same(Authorization, authenticated.Authorization);
        Assert.Equal(Authorization.Actor, authenticated.Actor);
        Assert.Equal(WorkInvocationChannel.SignalR, authenticated.Channel);
        Assert.Equal(WorkOriginSurface.WorkableAdapter, authenticated.Surface);
        Assert.True(authenticated.IsAuthenticated);
        Assert.Same(unauthenticatedAuthorization, unauthenticated.Authorization);
        Assert.False(unauthenticated.IsAuthenticated);
    }

    [Fact]
    public async Task IsolateUnknownComponentsAndInvalidRunIdentifiersPerComponent()
    {
        var result = await WorkableRealtimeWorkflowViews.Query(
            new UnsupportedWorkSystem(),
            Authorization,
            "workflow-run",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("unknown", "not-a-workflow-component", Shape: WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "invalid-run",
                    "workflowRun",
                    JsonSerializer.SerializeToElement(new { runId = "not-a-guid" }),
                    WorkComponentShapes.Standard),
            ]),
            CancellationToken.None);

        Assert.Equal("error", result.Components["unknown"].Status);
        Assert.Contains("Unknown component", result.Components["unknown"].Error);
        Assert.Equal(WorkComponentShapes.Compact, result.Components["unknown"].Shape);
        Assert.Equal("error", result.Components["invalid-run"].Status);
        Assert.Contains("valid 'runId'", result.Components["invalid-run"].Error);
        Assert.Equal(WorkComponentShapes.Standard, result.Components["invalid-run"].Shape);
    }

    [Fact]
    public async Task ConvertUnsupportedWorkflowViewSystemsIntoComponentErrors()
    {
        var runId = Guid.NewGuid();
        var result = await WorkableRealtimeWorkflowViews.Query(
            new UnsupportedWorkSystem(),
            Authorization,
            "workflow-run",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "run",
                    "workflowRun",
                    JsonSerializer.SerializeToElement(new { runId, childSampleSize = 5 })),
            ]),
            CancellationToken.None);

        var component = Assert.Single(result.Components).Value;
        Assert.Equal("error", component.Status);
        Assert.Contains("built-in Workable", component.Error);
    }

    [Fact]
    public async Task RejectInvalidPublicInputs()
    {
        var system = new UnsupportedWorkSystem();
        var criteria = new WorkViewCriteria();

        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkableRealtimeWorkflowViews.Query(
            null!, Authorization, "workflow-runs", criteria, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkableRealtimeWorkflowViews.Query(
            system, null!, "workflow-runs", criteria, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => WorkableRealtimeWorkflowViews.Query(
            system, Authorization, " ", criteria, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkableRealtimeWorkflowViews.Query(
            system, Authorization, "workflow-runs", null!, CancellationToken.None));
    }

    private sealed class UnsupportedWorkSystem : IWorkSystem
    {
        public WorkSystemId Id { get; } = new(Guid.NewGuid());

        public string? Name => "unsupported";

        public bool RequiresAuthorization => false;

        public WorkSystemState State => WorkSystemState.Started;

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue => throw new NotSupportedException();

        public IWorkerOperations Workers => throw new NotSupportedException();

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public ValueTask<WorkSystemAccessSummary> DescribeAccess(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IWorkSystemSession> CreateSession(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Start(WorkRequestContext requestContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemStopResult> Stop(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
