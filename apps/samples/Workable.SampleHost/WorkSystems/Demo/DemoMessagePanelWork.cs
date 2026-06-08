using Workable;

namespace SampleHost.Demo;

public sealed record DemoMessagePanelInput();

public sealed record DemoMessagePanelOutput(
    int MessageCount,
    DateTimeOffset CompletedAt);

public sealed class DemoMessagePanelWork : IWorkExecutor<DemoMessagePanelInput, DemoMessagePanelOutput>
{
    private const int MessagesPerSeverity = 100;
    private const int MessageCount = MessagesPerSeverity * 4;

    public Task<WorkExecutionResult<DemoMessagePanelOutput>> Execute(
        IWorkExecutionContext context,
        DemoMessagePanelInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(WorkExecutionResult<DemoMessagePanelOutput>.Success(
            new DemoMessagePanelOutput(
                MessageCount,
                DateTimeOffset.UtcNow),
            CreateMessages()));
    }

    private static IReadOnlyList<WorkMessage> CreateMessages()
    {
        var messages = new List<WorkMessage>(MessageCount);

        for (var index = 1; index <= MessagesPerSeverity; index++)
        {
            messages.Add(CreateMessage(
                "sample.demo.message-panel.warning",
                WorkMessageSeverity.Warning,
                $"Message panel warning message {index} of {MessagesPerSeverity}.",
                "messages.warning",
                index));
        }

        for (var index = 1; index <= MessagesPerSeverity; index++)
        {
            messages.Add(CreateMessage(
                "sample.demo.message-panel.information",
                WorkMessageSeverity.Information,
                $"Message panel information message {index} of {MessagesPerSeverity}.",
                "messages.information",
                index));
        }

        for (var index = 1; index <= MessagesPerSeverity; index++)
        {
            messages.Add(CreateMessage(
                "sample.demo.message-panel.debug",
                WorkMessageSeverity.Debug,
                $"Message panel debug message {index} of {MessagesPerSeverity}.",
                "messages.debug",
                index));
        }

        for (var index = 1; index <= MessagesPerSeverity; index++)
        {
            messages.Add(CreateMessage(
                "sample.demo.message-panel.trace",
                WorkMessageSeverity.Trace,
                $"Message panel trace message {index} of {MessagesPerSeverity}.",
                "messages.trace",
                index));
        }

        return messages;
    }

    private static WorkMessage CreateMessage(
        string code,
        WorkMessageSeverity severity,
        string text,
        string target,
        int index)
        => new(
            code,
            severity,
            text,
            target,
            new Dictionary<string, object?>
            {
                ["index"] = index,
                ["severity"] = severity.ToString(),
                ["source"] = "sample.demo.message-panel",
            });
}
