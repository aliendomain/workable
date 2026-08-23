namespace Workable;

internal sealed class WorkAuthorizationGroupResolver(
    IEnumerable<IWorkAuthorizationGroupContextProvider> contextProviders,
    IWorkAuthorizationGroupProvider actorProvider) : IWorkAuthorizationGroupResolver
{
    private readonly IReadOnlyList<IWorkAuthorizationGroupContextProvider> contextProviders =
        [.. contextProviders.OrderBy(provider => provider.Order)];

    public async ValueTask<IReadOnlySet<string>> GetGroups(
        WorkRequestContext requestContext,
        string? systemName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (requestContext.Authorization is { Scope: { } scope } snapshot &&
            snapshot.Actor == requestContext.Actor &&
            scope.IsForSystem(systemName))
        {
            return snapshot.Groups;
        }

        try
        {
            foreach (var contextProvider in this.contextProviders)
            {
                var groups = await contextProvider.GetCurrentGroups(
                    requestContext.Actor,
                    systemName,
                    cancellationToken);
                if (groups is not null)
                {
                    return WorkAuthorizationGroups.Normalize(groups);
                }
            }

            return WorkAuthorizationGroups.Normalize(await actorProvider.GetGroups(
                requestContext.Actor,
                systemName,
                cancellationToken));
        }
        catch (Exception exception) when (WorkAuthorizationGroupResolutionException.CanWrap(exception))
        {
            throw new WorkAuthorizationGroupResolutionException(systemName, exception);
        }
    }
}
