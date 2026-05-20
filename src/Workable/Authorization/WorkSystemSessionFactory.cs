namespace Workable;

internal sealed class WorkSystemSessionFactory(
    WorkSystemId systemId,
    string? systemName,
    IWorkCatalog catalog,
    IWorkQueueService queue,
    IWorkerOperations workers,
    IWorkQueryService query,
    IWorkEventStream events,
    IWorkAuthorizationScopeProvider scopeProvider)
{
    private readonly IWorkSystemSession directSession = new WorkSystemSession(catalog, queue, workers, query, events);

    public IWorkSystemSession CreateDirectSession()
        => this.directSession;

    public IWorkSystemSession CreateAuthorizedSession(WorkActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var scope = scopeProvider.GetScope(actor, systemId, systemName) ?? WorkAuthorizationScope.Empty;
        return new WorkSystemSession(
            new AuthorizedWorkCatalog(catalog, scope),
            new AuthorizedWorkQueueService(catalog, queue, scope),
            new AuthorizedWorkerOperations(workers, query, scope),
            new AuthorizedWorkQueryService(catalog, query, scope),
            new AuthorizedWorkEventStream(events, scope));
    }
}
