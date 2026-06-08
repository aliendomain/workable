using Workable;

namespace Workable.SampleHost.Demo;

public sealed record DemoTimedInput(
    string Scenario,
    int DelayMilliseconds,
    bool ShouldFail = false,
    string? DiscoveredIdentifierType = null,
    string? DiscoveredIdentifierValue = null,
    bool UseTaskYield = false);

public sealed record DemoTimedOutput(
    string Scenario,
    int DelayMilliseconds,
    DateTimeOffset CompletedAt);

public sealed class DemoTimedWork : IWorkExecutor<DemoTimedInput, DemoTimedOutput>
{
    public async Task<WorkExecutionResult<DemoTimedOutput>> Execute(
        IWorkExecutionContext context,
        DemoTimedInput input,
        CancellationToken cancellationToken)
    {
        var delayMilliseconds = input.UseTaskYield
            ? 0
            : Math.Clamp(input.DelayMilliseconds, 500, 10_000);
        if (!string.IsNullOrWhiteSpace(input.DiscoveredIdentifierType) &&
            !string.IsNullOrWhiteSpace(input.DiscoveredIdentifierValue))
        {
            context.AddIdentifier(new WorkIdentifier(input.DiscoveredIdentifierType, input.DiscoveredIdentifierValue));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (input.UseTaskYield)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            await Task.Delay(delayMilliseconds, cancellationToken);
        }

        var output = new DemoTimedOutput(
            input.Scenario,
            delayMilliseconds,
            DateTimeOffset.UtcNow);

        if (input.ShouldFail)
        {
            return WorkExecutionResult<DemoTimedOutput>.Failure(
                [WorkMessage.Error("sample.demo.failed", $"Demo scenario '{input.Scenario}' failed intentionally.", "shouldFail")],
                output);
        }

        return WorkExecutionResult<DemoTimedOutput>.Success(
            output,
            [WorkMessage.Info("sample.demo.completed", input.UseTaskYield
                ? $"Demo scenario '{input.Scenario}' completed after yielding once."
                : $"Demo scenario '{input.Scenario}' completed after {delayMilliseconds}ms.")]);
    }
}
