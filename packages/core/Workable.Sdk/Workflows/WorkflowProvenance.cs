namespace Workable;

/// <summary>
/// Identifies the workflow dispatch that created a worker.
/// </summary>
/// <remarks>
/// Workable assigns this provenance through its internal workflow queue path. Queue callers cannot assign workflow
/// provenance through <see cref="WorkInput"/> identifiers; persistence providers should retain this value unchanged.
/// </remarks>
/// <param name="RunId">The workflow run that dispatched the worker.</param>
/// <param name="DefinitionName">The workflow definition that owns the run.</param>
/// <param name="StepName">The workflow step that dispatched the worker.</param>
public sealed record WorkflowProvenance(
    WorkflowRunId RunId,
    string DefinitionName,
    string StepName);
