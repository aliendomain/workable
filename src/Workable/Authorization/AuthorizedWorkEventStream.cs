namespace Workable;

internal sealed class AuthorizedWorkEventStream(IWorkEventStream inner, WorkAuthorizationScope scope) : IWorkEventStream
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
        var readable = scope.ReadDefinitionIds;
        if (readable.Count == 0)
        {
            return null;
        }

        if (filter?.DefinitionId is { } definitionId)
        {
            return readable.Contains(definitionId) ? filter : null;
        }

        var definitionIds = filter?.DefinitionIds is { Count: > 0 } requested
            ? requested.Where(readable.Contains).ToHashSet()
            : readable;

        return definitionIds.Count == 0
            ? null
            : (filter ?? new WorkEventFilter()) with { DefinitionIds = definitionIds };
    }
}
