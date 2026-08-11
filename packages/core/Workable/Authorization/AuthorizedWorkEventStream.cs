namespace Workable;

internal sealed class AuthorizedWorkEventStream(
    IWorkEventStream inner,
    IReadOnlySet<string> readableWorkDefinitionNames,
    IReadOnlySet<string> readableWorkflowDefinitionNames) : IWorkEventStream
{
    public IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null)
    {
        var authorizedFilter = this.CreateAuthorizedFilter(filter);
        return authorizedFilter is null
            ? EmptyWorkEventSubscription.Instance
            : inner.Subscribe(authorizedFilter, options);
    }

    private WorkEventFilter? CreateAuthorizedFilter(WorkEventFilter? filter)
    {
        var readableDefinitions = CreateReadableDefinitions();
        if (readableDefinitions.Count == 0)
        {
            return null;
        }

        if (filter?.DefinitionKind is { } requestedKind)
        {
            readableDefinitions.RemoveWhere(definition => definition.Kind != requestedKind);
        }

        if (!string.IsNullOrWhiteSpace(filter?.DefinitionName))
        {
            readableDefinitions.RemoveWhere(definition =>
                !string.Equals(definition.Name, filter.DefinitionName, StringComparison.OrdinalIgnoreCase));
            return readableDefinitions.Count == 0
                ? null
                : filter with { AuthorizedDefinitions = readableDefinitions };
        }

        if (filter?.DefinitionNames is { Count: > 0 } requested)
        {
            readableDefinitions.RemoveWhere(definition =>
                !requested.Contains(definition.Name, StringComparer.OrdinalIgnoreCase));
        }

        return readableDefinitions.Count == 0
            ? null
            : (filter ?? new WorkEventFilter()) with
            {
                DefinitionNames = readableDefinitions
                    .Select(static definition => definition.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                AuthorizedDefinitions = readableDefinitions,
            };
    }

    private HashSet<WorkEventDefinitionScope> CreateReadableDefinitions()
        => readableWorkDefinitionNames
            .Select(static name => new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, name))
            .Concat(readableWorkflowDefinitionNames.Select(static name =>
                new WorkEventDefinitionScope(WorkEventDefinitionKind.Workflow, name)))
            .ToHashSet(WorkEventDefinitionScopeComparer.Instance);
}
