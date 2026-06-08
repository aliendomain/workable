using Workable;

namespace Workable.SampleHost.Operations;

public sealed record SampleEchoInput(string Message);

public sealed record SampleEchoOutput(string Message);

[WorkMetadata("sample.echo", "Samples:Basic", "Returns the submitted message.")]
public sealed class SampleEchoWork : IWorkExecutor<SampleEchoInput, SampleEchoOutput>
{
    public Task<WorkExecutionResult<SampleEchoOutput>> Execute(
        IWorkExecutionContext context,
        SampleEchoInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<SampleEchoOutput>.Success(new SampleEchoOutput(input.Message)));
}

public sealed record SampleDelayInput(int DelayMilliseconds = 1_000);

public sealed record SampleDelayOutput(int DelayedMilliseconds, DateTimeOffset CompletedAt);

[WorkMetadata("sample.delay", "Samples:Timing", "Waits briefly and returns timing details.")]
public sealed class SampleDelayWork : IWorkExecutor<SampleDelayInput, SampleDelayOutput>
{
    public async Task<WorkExecutionResult<SampleDelayOutput>> Execute(
        IWorkExecutionContext context,
        SampleDelayInput input,
        CancellationToken cancellationToken)
    {
        var delayMilliseconds = Math.Clamp(input.DelayMilliseconds, 0, 30_000);
        await Task.Delay(delayMilliseconds, cancellationToken);

        return WorkExecutionResult<SampleDelayOutput>.Success(new SampleDelayOutput(
            delayMilliseconds,
            DateTimeOffset.UtcNow));
    }
}

[WorkMetadata("ops.health.snapshot", "Operations:Health", "Returns a quick sample health payload without requiring input.")]
public sealed class HealthSnapshotWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new HealthSnapshotOutput(
            "ok",
            Environment.MachineName,
            DateTimeOffset.UtcNow,
            Random.Shared.Next(1, 25)))));
}

public sealed record HealthSnapshotOutput(
    string Status,
    string HostName,
    DateTimeOffset CheckedAt,
    int ActiveWorkers);
