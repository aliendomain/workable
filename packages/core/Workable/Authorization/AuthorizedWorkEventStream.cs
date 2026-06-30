namespace Workable;

internal sealed class AuthorizedWorkEventStream(
    IWorkEventStream inner,
    IReadOnlySet<string> readableDefinitionNames) : IWorkEventStream
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
        if (readableDefinitionNames.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(filter?.DefinitionName))
        {
            return readableDefinitionNames.Contains(filter.DefinitionName) ? filter : null;
        }

        var definitionNames = filter?.DefinitionNames is { Count: > 0 } requested
            ? requested.Where(readableDefinitionNames.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : readableDefinitionNames;

        return definitionNames.Count == 0
            ? null
            : (filter ?? new WorkEventFilter()) with { DefinitionNames = definitionNames };
    }
}
