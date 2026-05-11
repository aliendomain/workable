namespace Workable;
public sealed record WorkCompletion(
    WorkCompletionStatus Status,
    WorkerSnapshot? Worker,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsCompletedSuccessfully => this.Status == WorkCompletionStatus.Completed;

    public WorkCompletion<TOutput> ToTyped<TOutput>()
        => new(
            this.Status,
            this.Worker,
            this.Output is null ? default : this.Output.ToValue<TOutput>(),
            this.Output,
            this.Messages);
}

public sealed record WorkCompletion<TOutput>(
    WorkCompletionStatus Status,
    WorkerSnapshot? Worker,
    TOutput? Output,
    WorkOutput? RawOutput,
    IReadOnlyList<WorkMessage> Messages)
{
    public bool IsCompletedSuccessfully => this.Status == WorkCompletionStatus.Completed;
}
