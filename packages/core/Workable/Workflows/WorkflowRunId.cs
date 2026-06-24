namespace Workable;

internal readonly record struct WorkflowRunId(Guid Value)
{
    public static WorkflowRunId New() => new(Guid.NewGuid());

    public override string ToString() => this.Value.ToString("D");
}
