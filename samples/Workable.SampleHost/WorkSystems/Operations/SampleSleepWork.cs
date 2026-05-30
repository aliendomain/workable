using Workable;

namespace Workable.SampleHost.Operations;

public sealed record SampleSleepInput(int Milliseconds = 1_000);

public sealed record SampleSleepOutput(int Milliseconds, DateTimeOffset CompletedAt);

[WorkMetadata("sample.sleep", "Samples:Timing", "Sleeps for the requested number of milliseconds.")]
public sealed class SampleSleepWork : IWorkExecutor<SampleSleepInput, SampleSleepOutput>
{
    public async Task<WorkExecutionResult<SampleSleepOutput>> Execute(
        IWorkExecutionContext context,
        SampleSleepInput input,
        CancellationToken cancellationToken)
    {
        var milliseconds = Math.Clamp(input.Milliseconds, 0, 300_000);
        await Task.Delay(milliseconds, cancellationToken);

        return WorkExecutionResult<SampleSleepOutput>.Success(
            new SampleSleepOutput(milliseconds, DateTimeOffset.UtcNow));
    }
}
