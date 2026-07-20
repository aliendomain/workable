using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerActions")]
public sealed class WorkerOperationsShould
{
    [Fact]
    public async Task HonorCanceledBulkActionTokensBeforeMatchingWorkers()
    {
        var system = CreateSystem();
        await system.Start();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            system.Workers.ExecuteAll(
                WorkAction.Cancel,
                new WorkerBulkActionFilter(Category: "NoMatches"),
                cancellation.Token));
    }

    [Fact]
    public async Task ReturnStableMissingOutcomesForWorkerAndCategoryTargets()
    {
        var system = CreateSystem();
        await system.Start();
        var workerId = WorkerId.New();

        var reconfigure = await system.Workers.Reconfigure(
            new WorkerVersion(workerId, 1),
            new WorkerReconfiguration());
        var bulk = await system.Workers.ExecuteAll(
            WorkAction.Cancel,
            new WorkerBulkActionFilter(Category: "missing.category", IncludeSubcategories: true));

        Assert.Equal(WorkActionStatus.NotFound, reconfigure.Status);
        Assert.Equal(workerId, reconfigure.WorkerId);
        Assert.Equal(0, bulk.MatchedWorkerCount);
        Assert.Empty(bulk.Outcomes);
    }

    private static IWorkSystem CreateSystem()
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    "worker.operations.cancellation",
                    configuration: WorkConfiguration.Default with
                    {
                        Start = WorkStartConfiguration.DoNotStart,
                    }),
                SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
