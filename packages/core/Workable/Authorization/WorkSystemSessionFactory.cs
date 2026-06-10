namespace Workable;

internal sealed class WorkSystemSessionFactory(
    WorkSystemId systemId,
    string? systemName,
    Func<WorkSystemState> getSystemState,
    WorkSystemDiagnostics diagnostics,
    WorkSystemCatalog catalog,
    WorkQueueService queue,
    WorkerOperations workers,
    WorkSystemReadModelQueryService query,
    WorkEventStream events,
    WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
    IWorkAuthorizationGroupProvider groupProvider)
{
    public IWorkSystemSession CreateSession(WorkRequestContext requestContext, bool requiresAuthorization)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        var sessionDiagnostics = new SessionWorkSystemDiagnostics(diagnostics, requestContext);
        var sessionCatalog = new SessionWorkCatalog(catalog, requestContext);
        var sessionQueue = new SessionWorkQueueService(queue, requestContext);
        var sessionWorkers = new SessionWorkerOperations(workers, requestContext);
        var sessionQuery = new SessionWorkQueryService(query, requestContext);
        var sessionEvents = new SessionWorkEventStream(events, requestContext);
        if (!requiresAuthorization)
        {
            return new WorkSystemSession(
                systemName,
                getSystemState,
                sessionDiagnostics,
                sessionCatalog,
                sessionQueue,
                sessionWorkers,
                sessionQuery,
                sessionEvents);
        }

        var groups = requestContext.Authorization?.Groups
            ?? groupProvider.GetGroups(requestContext.Actor, systemName)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(systemAuthorizationConfiguration, groups);
        var authorization = new WorkAuthorizationEvaluator(
            catalog,
            groups,
            requestContext.IsAuthenticated && requestContext.Actor.IsKnown,
            systemAuthorization);
        return new WorkSystemSession(
            systemName,
            getSystemState,
            systemAuthorization.CanViewDiagnostics()
                ? sessionDiagnostics
                : new UnauthorizedWorkSystemDiagnostics(systemId, systemName),
            new AuthorizedWorkCatalog(sessionCatalog, authorization),
            new AuthorizedWorkQueueService(catalog, sessionQueue, authorization, requestContext),
            new AuthorizedWorkerOperations(catalog, sessionWorkers, sessionQuery, authorization, requestContext),
            new AuthorizedWorkQueryService(sessionCatalog, sessionQuery, authorization),
            new AuthorizedWorkEventStream(sessionEvents, authorization));
    }
}
