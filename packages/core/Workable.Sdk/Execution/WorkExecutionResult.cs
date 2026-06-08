namespace Workable;
/// <summary>
/// Represents the outcome of one work execution when the output is already in raw <see cref="WorkOutput"/> form.
/// </summary>
/// <param name="Output">The output payload to retain for the worker, or <see langword="null"/> when the execution produced no output.</param>
/// <param name="Messages">Structured messages emitted by the execution.</param>
public sealed record WorkExecutionResult(
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Creates a successful execution result.
    /// </summary>
    /// <param name="output">The output payload to retain for the worker, or <see langword="null"/> for no output.</param>
    /// <param name="messages">Optional structured messages to retain alongside the successful execution.</param>
    /// <returns>A successful execution result.</returns>
    public static WorkExecutionResult Success(WorkOutput? output = null, IEnumerable<WorkMessage>? messages = null)
        => new(output, [.. messages ?? []]);

    /// <summary>
    /// Creates a failed execution result.
    /// </summary>
    /// <param name="messages">The structured failure messages that explain why execution failed.</param>
    /// <param name="output">Optional output to retain alongside the failure.</param>
    /// <returns>A failed execution result.</returns>
    public static WorkExecutionResult Failure(IEnumerable<WorkMessage> messages, WorkOutput? output = null)
        => new(output, [.. messages]);

    /// <summary>
    /// Gets a value indicating whether any retained message has an error or more severe classification.
    /// </summary>
    public bool HasErrors => this.Messages.Any(message => message.Severity.IsError());
}
