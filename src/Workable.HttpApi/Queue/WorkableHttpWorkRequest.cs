using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents the HTTP request body used to queue work by definition name.
/// </summary>
/// <param name="Input">The optional JSON input payload for the work definition.</param>
/// <param name="Completion">Whether the HTTP response returns after acceptance or waits for terminal completion.</param>
/// <param name="Options">Optional per-request worker option and runtime configuration overrides.</param>
/// <param name="SubjectId">An optional subject identifier to attach to the queued worker input.</param>
/// <param name="ConcurrencyKey">An optional concurrency key to attach to the queued worker input.</param>
/// <param name="Identifiers">Optional additional identifiers to attach to the queued worker input.</param>
/// <param name="Description">An optional human-readable request description stored on the worker origin.</param>
public sealed record WorkableHttpWorkRequest(
    JsonElement? Input = null,
    WorkableHttpCompletion Completion = WorkableHttpCompletion.ReturnAfterAccepted,
    WorkableHttpWorkerOptions? Options = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    IReadOnlySet<WorkIdentifier>? Identifiers = null,
    string? Description = null);
