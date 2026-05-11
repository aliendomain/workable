using Workable;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWorkableSystem(workable =>
{
    workable.StartWithHost();
    workable.AddWork<SampleEchoWork>();
    workable.AddWork<SampleDelayWork>();
});

builder.Services.AddWorkableHttpApi();
builder.Services.AddWorkableMcpServer();

var app = builder.Build();

app.MapGet("/", () => Results.Json(new
{
    Workable = "/workable",
    WorkableDefinitions = "/workable/definitions",
    Mcp = "/mcp",
}));

app.MapWorkableApi("/workable");
app.MapWorkableMcp("/mcp");

await app.RunAsync();

public sealed record SampleEchoInput(string Message);

public sealed record SampleEchoOutput(string Message);

[WorkMetadata("sample.echo", "Samples", "Returns the submitted message.")]
[WorkInvocation(WorkInvocationChannel.Mcp)]
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

[WorkMetadata("sample.delay", "Samples", "Waits briefly and returns timing details.")]
[WorkInvocation(WorkInvocationChannel.Mcp)]
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
