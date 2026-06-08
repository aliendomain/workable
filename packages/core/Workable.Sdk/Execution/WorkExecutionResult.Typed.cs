namespace Workable;

/// <summary>
/// Represents the outcome of one work execution when the executor returns typed output.
/// </summary>
/// <typeparam name="TOutput">The logical output type that Workable serializes into retained worker output.</typeparam>
/// <param name="Output">The typed output value to retain for the worker, or <see langword="null"/> when the execution produced no output.</param>
/// <param name="Messages">Structured messages emitted by the execution.</param>
public sealed record WorkExecutionResult<TOutput>(
    TOutput? Output,
    IReadOnlyList<WorkMessage> Messages) : IUntypedWorkExecutionResult
{
    /// <summary>
    /// Creates a successful execution result with typed output.
    /// </summary>
    /// <param name="output">The typed output value to retain for the worker, or <see langword="null"/> for no output.</param>
    /// <param name="messages">Optional structured messages to retain alongside the successful execution.</param>
    /// <returns>A successful execution result.</returns>
    public static WorkExecutionResult<TOutput> Success(
        TOutput? output,
        IEnumerable<WorkMessage>? messages = null)
        => new(output, [.. messages ?? []]);

    /// <summary>
    /// Creates a failed execution result with optional typed output.
    /// </summary>
    /// <param name="messages">The structured failure messages that explain why execution failed.</param>
    /// <param name="output">Optional typed output to retain alongside the failure.</param>
    /// <returns>A failed execution result.</returns>
    public static WorkExecutionResult<TOutput> Failure(
        IEnumerable<WorkMessage> messages,
        TOutput? output = default)
        => new(output, [.. messages]);

    /// <summary>
    /// Gets a value indicating whether any retained message has an error or more severe classification.
    /// </summary>
    public bool HasErrors => this.Messages.Any(message => message.Severity.IsError());

    WorkExecutionResult IUntypedWorkExecutionResult.ToUntyped()
        => new(
            this.Output is null ? null : WorkOutput.FromValue(this.Output, WorkData.DefaultJsonOptions),
            this.Messages);
}

internal interface IUntypedWorkExecutionResult
{
    WorkExecutionResult ToUntyped();
}
