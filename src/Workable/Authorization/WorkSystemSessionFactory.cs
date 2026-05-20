namespace Workable;

internal sealed class WorkSystemSessionFactory(
    WorkSystemId systemId,
    string? systemName,
    Func<WorkSystemState> getSystemState,
    IWorkSystemDiagnostics diagnostics,
    IWorkCatalog catalog,
    IRequestContextWorkQueueService queue,
    IRequestContextWorkerOperations workers,
    IWorkQueryService query,
    IWorkEventStream events,
    WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
    IWorkAuthorizationGroupProvider groupProvider)
{
    public IWorkSystemSession CreateSession(WorkRequestContext requestContext, bool requiresAuthorization)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        var sessionQueue = new SessionWorkQueueService(queue, requestContext);
        var sessionWorkers = new SessionWorkerOperations(workers, requestContext);
        if (!requiresAuthorization)
        {
            return new WorkSystemSession(
                systemName,
                getSystemState,
                diagnostics,
                catalog,
                catalog,
                sessionQueue,
                sessionWorkers,
                query,
                events);
        }

        var groups = requestContext.Authorization?.Groups
            ?? groupProvider.GetGroups(requestContext.Actor, systemName)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(systemAuthorizationConfiguration, groups);
        var authorization = new WorkAuthorizationEvaluator(catalog, groups, systemAuthorization);
        return new WorkSystemSession(
            systemName,
            getSystemState,
            systemAuthorization.CanViewDiagnostics()
                ? diagnostics
                : new UnauthorizedWorkSystemDiagnostics(systemId, systemName),
            catalog,
            new AuthorizedWorkCatalog(catalog, authorization),
            new AuthorizedWorkQueueService(catalog, sessionQueue, authorization),
            new AuthorizedWorkerOperations(sessionWorkers, query, authorization),
            new AuthorizedWorkQueryService(catalog, query, authorization),
            new AuthorizedWorkEventStream(events, authorization));
    }
}
