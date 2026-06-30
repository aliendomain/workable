namespace Workable;

/// <summary>
/// Identifies one workflow run.
/// </summary>
public readonly record struct WorkflowRunId(Guid Value)
{
    /// <summary>
    /// Creates a new workflow run identifier.
    /// </summary>
    public static WorkflowRunId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => this.Value.ToString("D");
}
