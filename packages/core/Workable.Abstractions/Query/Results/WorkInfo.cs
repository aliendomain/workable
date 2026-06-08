namespace Workable;

/// <summary>
/// Represents one definition plus its current compact worker posture.
/// </summary>
/// <param name="Definition">The registered work definition.</param>
/// <param name="Status">The definition's current operational status.</param>
/// <param name="Workers">The compact worker rollup for the definition.</param>
public sealed record WorkInfo(
    WorkDefinition Definition,
    WorkDefinitionStatus Status,
    WorkerRollup Workers) : IWorkQueryResult;
