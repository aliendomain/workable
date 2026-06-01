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
