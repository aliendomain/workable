namespace Workable;

/// <summary>
/// Identifies the definition namespace that produced a work event.
/// </summary>
public enum WorkEventDefinitionKind
{
    /// <summary>
    /// The event was produced by a work definition or worker.
    /// </summary>
    Work,

    /// <summary>
    /// The event was produced by a workflow definition or workflow run.
    /// </summary>
    Workflow,
}

internal readonly record struct WorkEventDefinitionScope(
    WorkEventDefinitionKind Kind,
    string Name);

internal sealed class WorkEventDefinitionScopeComparer : IEqualityComparer<WorkEventDefinitionScope>
{
    internal static WorkEventDefinitionScopeComparer Instance { get; } = new();

    public bool Equals(WorkEventDefinitionScope x, WorkEventDefinitionScope y)
        => x.Kind == y.Kind && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(WorkEventDefinitionScope obj)
        => HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
}
