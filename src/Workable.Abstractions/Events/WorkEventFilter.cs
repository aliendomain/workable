using System.Diagnostics.CodeAnalysis;

namespace Workable;
public sealed record WorkEventFilter(
    WorkerId? WorkerId = null,
    WorkDefinitionId? DefinitionId = null,
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    WorkIdentifier? Identifier = null,
    string? EventType = null)
{
    public bool Matches(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        return (this.WorkerId is null || this.WorkerId == workEvent.WorkerId) &&
            (this.DefinitionId is null || this.DefinitionId == workEvent.DefinitionId) &&
            (this.SubjectId is null || this.SubjectId == workEvent.SubjectId) &&
            (this.ConcurrencyKey is null || this.ConcurrencyKey == workEvent.ConcurrencyKey) &&
            (this.Identifier is null || workEvent.Identifiers.Contains(this.Identifier.Value)) &&
            (this.EventType is null || string.Equals(this.EventType, workEvent.EventType, StringComparison.OrdinalIgnoreCase));
    }
}
