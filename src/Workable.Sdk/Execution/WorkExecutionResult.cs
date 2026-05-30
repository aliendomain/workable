namespace Workable;
public sealed record WorkExecutionResult(
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    public static WorkExecutionResult Success(WorkOutput? output = null, IEnumerable<WorkMessage>? messages = null)
        => new(output, [.. messages ?? []]);

    public static WorkExecutionResult Failure(IEnumerable<WorkMessage> messages, WorkOutput? output = null)
        => new(output, [.. messages]);

    public bool HasErrors => this.Messages.Any(message => message.Severity.IsError());
}
