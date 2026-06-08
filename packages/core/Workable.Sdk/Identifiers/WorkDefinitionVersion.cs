namespace Workable;

/// <summary>
/// Identifies one optimistic-concurrency version of a work definition.
/// </summary>
/// <param name="DefinitionId">The definition identifier.</param>
/// <param name="Revision">The expected definition revision.</param>
public readonly record struct WorkDefinitionVersion(WorkDefinitionId DefinitionId, long Revision);
