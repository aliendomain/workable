namespace Workable;

public sealed record WorkExecutionResult<TOutput>(
    TOutput? Output,
    IReadOnlyList<WorkMessage> Messages) : IUntypedWorkExecutionResult
{
    public static WorkExecutionResult<TOutput> Success(
        TOutput? output,
        IEnumerable<WorkMessage>? messages = null)
        => new(output, [.. messages ?? []]);

    public static WorkExecutionResult<TOutput> Failure(
        IEnumerable<WorkMessage> messages,
        TOutput? output = default)
        => new(output, [.. messages]);

    public bool HasErrors => this.Messages.Any(message => message.Severity == WorkMessageSeverity.Error);

    WorkExecutionResult IUntypedWorkExecutionResult.ToUntyped()
        => new(
            this.Output is null ? null : WorkOutput.FromValue(this.Output, WorkData.DefaultJsonOptions),
            this.Messages);
}

internal interface IUntypedWorkExecutionResult
{
    WorkExecutionResult ToUntyped();
}
