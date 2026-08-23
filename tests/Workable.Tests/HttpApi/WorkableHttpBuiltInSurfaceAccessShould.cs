using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpBuiltInSurfaceAccessShould
{
    [Fact]
    public async Task UseFastAndCompatibilityAccessPaths()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(_ => { })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.HttpApi);

        Assert.True(await WorkableHttpBuiltInSurfaceAccess.IsAllowed(system, requestContext));
        Assert.False(await WorkableHttpBuiltInSurfaceAccess.IsAllowed(
            new CompatibilityWorkSystem(system),
            requestContext));
    }

    private sealed class CompatibilityWorkSystem(IWorkSystem inner) : IWorkSystem
    {
        public WorkSystemId Id => inner.Id;
        public string? Name => inner.Name;
        public bool RequiresAuthorization => inner.RequiresAuthorization;
        public WorkSystemState State => inner.State;
        public IWorkCatalog Catalog => inner.Catalog;
        public IWorkQueueService Queue => inner.Queue;
        public IWorkerOperations Workers => inner.Workers;
        public IWorkQueryService Query => inner.Query;
        public IWorkEventStream Events => inner.Events;
        public IWorkIterationStatusStream IterationStatuses => inner.IterationStatuses;
        public IWorkChangeStream Changes => inner.Changes;
        public IWorkSystemDiagnostics Diagnostics => inner.Diagnostics;

        public ValueTask<WorkSystemAccessSummary> DescribeAccess(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.DescribeAccess(requestContext, cancellationToken);

        public ValueTask<IWorkSystemSession> CreateSession(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.CreateSession(requestContext, cancellationToken);

        public Task Start(WorkRequestContext requestContext, CancellationToken cancellationToken = default)
            => inner.Start(requestContext, cancellationToken);

        public Task<WorkSystemStopResult> Stop(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.Stop(requestContext, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
