using System.Collections.Frozen;

namespace Workable;

internal static class WorkflowProvenanceRules
{
    internal const string RunIdentifierType = "workflow-run";

    internal static bool IsRunIdentifier(string? type)
        => string.Equals(type, RunIdentifierType, StringComparison.OrdinalIgnoreCase);

    internal static WorkInput? SnapshotInput(WorkInput? input)
        => input?.Identifiers is null
            ? input
            : input with
            {
                Identifiers = input.Identifiers.ToFrozenSet(),
            };

    internal static bool ContainsMalformedIdentifier(WorkInput? input)
        => input?.Identifiers?.Any(static identifier =>
            string.IsNullOrWhiteSpace(identifier.Type) ||
            string.IsNullOrWhiteSpace(identifier.Value)) == true;

    internal static bool ContainsRunIdentifier(WorkInput? input)
        => input?.Identifiers?.Any(static identifier => IsRunIdentifier(identifier.Type)) == true;

    internal static bool HasExactRunIdentifier(WorkInput? input, WorkflowRunId runId)
    {
        var identifiers = input?.Identifiers?
            .Where(static identifier => IsRunIdentifier(identifier.Type))
            .ToArray() ?? [];
        return identifiers.Length == 1 &&
            string.Equals(identifiers[0].Value, runId.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
