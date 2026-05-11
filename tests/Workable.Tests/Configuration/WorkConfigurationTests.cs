using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkConfigurationTests
{
    [Fact]
    public void ContributedWorkCanBeConfiguredAtBootstrap()
    {
        var definition = WorkDefinition.Create("contributed-config", "Configured while contributed.");
        var system = new ServiceCollection()
            .AddWorkableWork(
                definition,
                SuccessfulWork,
                configuration => configuration.RecurEvery(TimeSpan.FromMinutes(3)))
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "contributed-config");

        Assert.True(configured.Configuration.Recurrence.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(3), configured.Configuration.Recurrence.Interval);
    }

    [Fact]
    public async Task ExecutionContextReceivesEffectiveConfiguration()
    {
        var observed = new TaskCompletionSource<RecurrenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = WorkDefinition.Create("context-config", "Executor can read effective configuration.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(4)),
            });
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            observed.TrySetResult(new RecurrenceResult(
                context.Configuration.Recurrence.IsEnabled,
                context.Configuration.Recurrence.Interval));
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("context-config");
        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var worker = await system.Query.GetWorker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        await system.Workers.Execute(RequiredWorker(worker).Version, WorkAction.Cancel);
        await handle.WaitForCompletion();

        Assert.True(result.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(4), result.Interval);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker.");

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed record RecurrenceResult(bool IsEnabled, TimeSpan Interval);
}
