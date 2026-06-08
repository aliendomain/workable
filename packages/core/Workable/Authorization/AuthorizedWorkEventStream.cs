namespace Workable;

internal sealed class AuthorizedWorkEventStream(IWorkEventStream inner, WorkAuthorizationEvaluator authorization) : IWorkEventStream
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
        var readable = authorization.ReadableDefinitions()
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (readable.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(filter?.DefinitionName))
        {
            return readable.Contains(filter.DefinitionName) ? filter : null;
        }

        var definitionNames = filter?.DefinitionNames is { Count: > 0 } requested
            ? requested.Where(readable.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : readable;

        return definitionNames.Count == 0
            ? null
            : (filter ?? new WorkEventFilter()) with { DefinitionNames = definitionNames };
    }
}
