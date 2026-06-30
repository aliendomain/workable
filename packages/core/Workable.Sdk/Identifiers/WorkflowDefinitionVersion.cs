namespace Workable;

/// <summary>
/// Identifies one optimistic-concurrency version of a workflow definition.
/// </summary>
/// <param name="DefinitionId">The definition identifier.</param>
/// <param name="Revision">The expected definition revision.</param>
public readonly record struct WorkflowDefinitionVersion(WorkflowDefinitionId DefinitionId, long Revision);
