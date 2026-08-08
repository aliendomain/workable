namespace Workable;

internal sealed class WorkSystemSessionFactory(
    WorkSystemId systemId,
    string? systemName,
    WorkSystemCapabilities capabilities,
    Func<WorkSystemState> getSystemState,
    WorkSystemDiagnostics diagnostics,
    WorkSystemCatalog catalog,
    WorkflowCatalog workflows,
    WorkQueueService queue,
    WorkerOperations workers,
    WorkSystemReadModelQueryService query,
    WorkEventStream events,
    WorkIterationStatusStream iterationStatuses,
    WorkChangeStream changes,
    WorkSystemAuthorizationConfiguration systemAuthorizationConfiguration,
    IWorkAuthorizationGroupResolver groupResolver)
{
    public async ValueTask<IWorkSystemSession> CreateSession(
        WorkRequestContext requestContext,
        bool requiresAuthorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        requestContext = SanitizeRequestContext(requestContext, systemName, out var replaceAuthorization);
        if (!requiresAuthorization)
        {
            return this.CreateUnrestrictedSession(requestContext);
        }

        var groups = await groupResolver.GetGroups(requestContext, systemName, cancellationToken);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(systemAuthorizationConfiguration, groups);
        var canViewDiagnostics = systemAuthorization.CanViewDiagnostics();
        var authorization = new WorkAuthorizationEvaluator(
            catalog,
            groups,
            requestContext.IsAuthenticated && requestContext.Actor.IsKnown,
            systemAuthorization);
        var readableDefinitions = authorization.ReadableDefinitions();
        if (replaceAuthorization)
        {
            requestContext = requestContext with
            {
                Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                    systemName,
                    requestContext.Actor,
                    groups,
                    readableDefinitions.Select(static definition => definition.Id)),
            };
        }

        var sessionDiagnostics = new SessionWorkSystemDiagnostics(diagnostics, requestContext);
        var sessionCatalog = new SessionWorkCatalog(catalog, requestContext);
        var sessionQueue = new SessionWorkQueueService(queue, requestContext);
        var sessionWorkers = new SessionWorkerOperations(workers, requestContext);
        var sessionQuery = new SessionWorkQueryService(query, requestContext);
        var sessionEvents = new SessionWorkEventStream(events, requestContext);
        var sessionIterationStatuses = new SessionWorkIterationStatusStream(iterationStatuses, requestContext);
        var sessionChanges = new SessionWorkChangeStream(changes, requestContext);
        var isKnownAuthenticatedActor = requestContext.IsAuthenticated && requestContext.Actor.IsKnown;
        var hasReadAllWorkAccess = systemAuthorization.HasReadAllWorkAccess();
        var readableDefinitionNames = readableDefinitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readableWorkflows = hasReadAllWorkAccess
            ? workflows.Definitions
            : workflows.Definitions.Where(workflow => workflow.Authorization.CanRead(groups, isKnownAuthenticatedActor));
        foreach (var workflow in readableWorkflows)
        {
            readableDefinitionNames.Add(workflow.Name);
        }

        return new WorkSystemSession(
            systemName,
            requestContext,
            capabilities,
            getSystemState,
            canViewDiagnostics
                ? sessionDiagnostics
                : new UnauthorizedWorkSystemDiagnostics(systemId, systemName),
            new AuthorizedWorkCatalog(catalog, sessionCatalog, authorization, requestContext),
            new AuthorizedWorkQueueService(catalog, sessionQueue, authorization, requestContext, canViewDiagnostics),
            new AuthorizedWorkerOperations(catalog, sessionWorkers, sessionQuery, authorization, requestContext, canViewDiagnostics),
            new AuthorizedWorkQueryService(sessionCatalog, sessionQuery, authorization, canViewDiagnostics),
            new AuthorizedWorkEventStream(sessionEvents, readableDefinitionNames),
            new AuthorizedWorkIterationStatusStream(iterationStatuses, readableDefinitionNames),
            new AuthorizedWorkChangeStream(sessionChanges, authorization, canViewDiagnostics));
    }

    private IWorkSystemSession CreateUnrestrictedSession(WorkRequestContext requestContext)
    {
        var sessionDiagnostics = new SessionWorkSystemDiagnostics(diagnostics, requestContext);
        var sessionCatalog = new SessionWorkCatalog(catalog, requestContext);
        var sessionQueue = new SessionWorkQueueService(queue, requestContext);
        var sessionWorkers = new SessionWorkerOperations(workers, requestContext);
        var sessionQuery = new SessionWorkQueryService(query, requestContext);
        var sessionEvents = new SessionWorkEventStream(events, requestContext);
        var sessionIterationStatuses = new SessionWorkIterationStatusStream(iterationStatuses, requestContext);
        var sessionChanges = new SessionWorkChangeStream(changes, requestContext);
        return new WorkSystemSession(
            systemName,
            requestContext,
            capabilities,
            getSystemState,
            sessionDiagnostics,
            sessionCatalog,
            sessionQueue,
            sessionWorkers,
            sessionQuery,
            sessionEvents,
            sessionIterationStatuses,
            sessionChanges);
    }

    private static WorkRequestContext SanitizeRequestContext(
        WorkRequestContext requestContext,
        string? systemName,
        out bool replaceAuthorization)
    {
        if (requestContext.Authorization is not { } snapshot)
        {
            replaceAuthorization = false;
            return requestContext;
        }

        replaceAuthorization = snapshot.Actor != requestContext.Actor ||
            snapshot.Scope is not { } scope ||
            !scope.IsForSystem(systemName);
        return !replaceAuthorization
            ? requestContext
            : requestContext.WithoutAuthorization();
    }
}
