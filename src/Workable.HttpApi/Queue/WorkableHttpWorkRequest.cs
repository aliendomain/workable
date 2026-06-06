using System.Text.Json;

namespace Workable;

public sealed record WorkableHttpWorkRequest(
    JsonElement? Input = null,
    WorkableHttpCompletion Completion = WorkableHttpCompletion.ReturnAfterAccepted,
    WorkableHttpWorkerOptions? Options = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    IReadOnlySet<WorkIdentifier>? Identifiers = null,
    string? Description = null);
