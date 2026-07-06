using System.Text.Json;

namespace Workable;

/// <summary>
/// Describes one HTTP workflow-start request.
/// </summary>
/// <param name="Input">The optional JSON input payload for workflow steps bound to workflow input.</param>
/// <param name="Description">Optional caller description recorded in the workflow origin.</param>
/// <param name="Completion">Whether the API should return after acceptance or after the workflow completes.</param>
/// <param name="SubjectId">An optional subject identifier to attach to the workflow input.</param>
/// <param name="ConcurrencyKey">An optional concurrency key to attach to the workflow input.</param>
/// <param name="Identifiers">Optional additional identifiers to attach to the workflow input.</param>
public sealed record WorkableHttpWorkflowStartRequest(
    JsonElement? Input = null,
    string? Description = null,
    WorkableHttpCompletion Completion = WorkableHttpCompletion.ReturnAfterAccepted,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    IReadOnlyList<WorkIdentifier>? Identifiers = null);
