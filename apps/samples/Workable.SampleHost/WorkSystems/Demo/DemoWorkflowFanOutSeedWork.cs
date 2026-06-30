using Workable;

namespace Workable.SampleHost.Demo;

public sealed record DemoWorkflowFanOutSeedInput(
    string BatchName,
    int SeedDelayMilliseconds,
    IReadOnlyList<DemoWorkflowFanOutSeedItem> Items);

public sealed record DemoWorkflowFanOutSeedItem(
    string Scenario,
    int DelayMilliseconds,
    string StepIdentifier,
    bool ShouldFail = false);

public sealed record DemoWorkflowFanOutSeedOutput(
    string BatchName,
    IReadOnlyList<DemoTimedInput> Items,
    DateTimeOffset GeneratedAt);

public sealed class DemoWorkflowFanOutSeedWork : IWorkExecutor<DemoWorkflowFanOutSeedInput, DemoWorkflowFanOutSeedOutput>
{
    public async Task<WorkExecutionResult<DemoWorkflowFanOutSeedOutput>> Execute(
        IWorkExecutionContext context,
        DemoWorkflowFanOutSeedInput input,
        CancellationToken cancellationToken)
    {
        var delayMilliseconds = Math.Clamp(input.SeedDelayMilliseconds, 500, 10_000);
        await Task.Delay(delayMilliseconds, cancellationToken);

        var items = input.Items
            .Select(item => new DemoTimedInput(
                item.Scenario,
                item.DelayMilliseconds,
                item.ShouldFail,
                DiscoveredIdentifierType: "sample-workflow-step",
                DiscoveredIdentifierValue: item.StepIdentifier))
            .ToArray();

        var output = new DemoWorkflowFanOutSeedOutput(
            input.BatchName,
            items,
            DateTimeOffset.UtcNow);

        return WorkExecutionResult<DemoWorkflowFanOutSeedOutput>.Success(
            output,
            [WorkMessage.Info(
                "sample.demo.workflow.seed.generated",
                $"Generated {items.Length} dynamic child inputs for batch '{input.BatchName}'.")]);
    }
}
