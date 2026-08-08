using Workable;

namespace Workable.SampleHost.Demo;

public sealed record DemoAssistantStreamInput(
    string Prompt = "Explain why ordered iteration status streams are useful.",
    int ChunkDelayMilliseconds = 75);

public sealed record DemoAssistantStreamOutput(
    string MessageId,
    string Response,
    int ChunkCount,
    DateTimeOffset CompletedAt);

public sealed record DemoAssistantMessageStarted(string MessageId, string Role);

public sealed record DemoAssistantTextDelta(string MessageId, string Text);

public sealed record DemoAssistantMessageCompleted(string MessageId, string FinishReason, int ChunkCount);

[WorkMetadata(
    "sample.demo.assistant-stream",
    "Samples:Demo",
    "Simulates an assistant response by publishing ordered text chunks through the iteration status stream.")]
public sealed class DemoAssistantStreamWork : IWorkExecutor<DemoAssistantStreamInput, DemoAssistantStreamOutput>
{
    public async Task<WorkExecutionResult<DemoAssistantStreamOutput>> Execute(
        IWorkExecutionContext context,
        DemoAssistantStreamInput input,
        CancellationToken cancellationToken)
    {
        var prompt = string.IsNullOrWhiteSpace(input.Prompt)
            ? "Explain why ordered iteration status streams are useful."
            : input.Prompt.Trim();
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(input.ChunkDelayMilliseconds, 0, 1_000));
        var messageId = Guid.NewGuid().ToString("N");
        var response =
            $"Workable is responding to: {prompt} " +
            "Each chunk is an application-defined status item ordered within this work iteration.";
        var chunks = SplitIntoWordChunks(response);

        context.Status.Publish(
            "assistant.message.started",
            new DemoAssistantMessageStarted(messageId, Role: "assistant"));
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Status.Publish(
                "assistant.text.delta",
                new DemoAssistantTextDelta(messageId, chunk));
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        context.Status.Publish(
            "assistant.message.completed",
            new DemoAssistantMessageCompleted(messageId, FinishReason: "stop", chunks.Count));

        return WorkExecutionResult<DemoAssistantStreamOutput>.Success(new DemoAssistantStreamOutput(
            messageId,
            response,
            chunks.Count,
            DateTimeOffset.UtcNow));
    }

    private static IReadOnlyList<string> SplitIntoWordChunks(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words
            .Select((word, index) => index == words.Length - 1 ? word : $"{word} ")
            .ToArray();
    }
}
