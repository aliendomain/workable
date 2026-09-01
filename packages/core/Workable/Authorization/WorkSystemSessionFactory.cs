using System.Runtime.CompilerServices;

namespace Workable;

internal sealed class WorkSystemSessionFactory(
    WorkSystemId systemId,
    string? systemName,
    Func<WorkSystemCapabilities> getCapabilities,
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
    private const int MaximumCachedAuthorizationProjections = 256;
    private readonly Lock projectionCacheSync = new();
    private readonly Dictionary<AuthorizationProjectionKey, CanonicalAuthorizationProjection> projectionCache =
        new(AuthorizationProjectionKeyComparer.Instance);
    private readonly Queue<AuthorizationProjectionKey> projectionCacheOrder = [];
    // Object identity is the trust boundary: public snapshots and record clones may supply groups,
    // but only the exact immutable snapshot issued by this factory can skip snapshot regeneration.
    private readonly ConditionalWeakTable<WorkAuthorizationSnapshot, CanonicalAuthorizationProjection>
        canonicalSnapshots = new();
    private IReadOnlyCollection<WorkDefinition> cachedWorkDefinitions = catalog.Definitions;
    private IReadOnlyList<WorkflowDefinition> cachedWorkflowDefinitions = workflows.Definitions;

    public async ValueTask<IWorkSystemSession> CreateSession(
        WorkRequestContext requestContext,
        bool requiresAuthorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        requestContext = SanitizeRequestContext(requestContext, systemName, out _);
        if (!requiresAuthorization)
        {
            return this.CreateUnrestrictedSession(requestContext);
        }

        var groups = WorkAuthorizationGroups.Normalize(
            await groupResolver.GetGroups(requestContext, systemName, cancellationToken));
        var isKnownAuthenticatedActor = requestContext.IsAuthenticated && requestContext.Actor.IsKnown;
        var projectionResolution = this.ResolveProjection(
            requestContext.Actor,
            groups,
            isKnownAuthenticatedActor,
            requestContext.IsAuthenticated);
        var projection = projectionResolution.Projection;
        var snapshot = this.TryReuseCanonicalSnapshot(requestContext, projection)
            ? requestContext.Authorization!
            : projectionResolution.CreatedSnapshot
                ?? CreateSnapshot(requestContext.Actor, projection);
        this.RegisterCanonicalSnapshot(snapshot, projection);
        requestContext = requestContext with
        {
            Authorization = snapshot,
        };

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
            getCapabilities(),
            getSystemState,
            (worker, changes) => this.CanReconfigureWorker(
                projection.Authorization,
                requestContext,
                worker,
                changes),
            projection.CanViewDiagnostics
                ? sessionDiagnostics
                : new UnauthorizedWorkSystemDiagnostics(systemId, systemName),
            new AuthorizedWorkDiscoveryCatalog(catalog, projection.Authorization),
            new AuthorizedWorkCatalog(catalog, sessionCatalog, projection.Authorization, requestContext),
            new AuthorizedWorkQueueService(
                catalog,
                sessionQueue,
                projection.Authorization,
                requestContext,
                projection.CanViewDiagnostics),
            new AuthorizedWorkerOperations(
                catalog,
                sessionWorkers,
                workers,
                sessionQuery,
                projection.Authorization,
                requestContext,
                projection.CanViewDiagnostics),
            new AuthorizedWorkQueryService(
                sessionCatalog,
                sessionQuery,
                projection.Authorization,
                projection.CanViewDiagnostics),
            new AuthorizedWorkEventStream(
                sessionEvents,
                projection.ReadableWorkDefinitionNames,
                projection.ReadableWorkflowDefinitionNames),
            new AuthorizedWorkIterationStatusStream(
                iterationStatuses,
                projection.ReadableWorkDefinitionNames,
                projection.CanViewDiagnostics),
            new AuthorizedWorkChangeStream(
                sessionChanges,
                projection.Authorization,
                projection.CanViewDiagnostics));
    }

    private ProjectionResolution ResolveProjection(
        WorkActor actor,
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedActor,
        bool isAuthenticated)
    {
        var key = new AuthorizationProjectionKey(groups, isKnownAuthenticatedActor, isAuthenticated);
        lock (this.projectionCacheSync)
        {
            // Definition collections are replaced whenever the runtime catalog is rebuilt.
            this.InvalidateProjectionCacheIfDefinitionsChanged();
            if (this.projectionCache.TryGetValue(key, out var cached))
            {
                return new ProjectionResolution(cached, CreatedSnapshot: null);
            }

            var systemAuthorization = new WorkSystemAuthorizationEvaluator(
                systemAuthorizationConfiguration,
                groups);
            var authorization = new WorkAuthorizationEvaluator(
                catalog,
                groups,
                isKnownAuthenticatedActor,
                systemAuthorization);
            var readableDefinitions = authorization.ReadableDefinitions();
            var readableWorkDefinitionNames = readableDefinitions
                .Select(static definition => definition.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var readableWorkflows = (systemAuthorization.HasReadAllWorkAccess()
                ? workflows.Definitions
                : workflows.Definitions.Where(workflow =>
                    workflow.Authorization.CanRead(groups, isKnownAuthenticatedActor)))
                .ToArray();
            var readableWorkflowDefinitionNames = readableWorkflows
                .Select(static workflow => workflow.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshot = WorkAuthorizationSnapshot.CreateForSystem(
                systemName,
                actor,
                groups,
                readableDefinitions.Select(static definition => definition.Id),
                readableWorkflowDefinitionIds: readableWorkflows.Select(static definition => definition.Id),
                canViewDiagnostics: systemAuthorization.CanViewDiagnostics(),
                isAuthenticated: isAuthenticated);
            var projection = new CanonicalAuthorizationProjection(
                snapshot.Groups,
                snapshot.Scope ?? new WorkAuthorizationScope(systemName),
                snapshot.ReadFingerprint,
                isAuthenticated,
                systemAuthorization.CanViewDiagnostics(),
                authorization,
                readableWorkDefinitionNames,
                readableWorkflowDefinitionNames);

            if (this.projectionCache.Count >= MaximumCachedAuthorizationProjections)
            {
                var oldest = this.projectionCacheOrder.Dequeue();
                this.projectionCache.Remove(oldest);
            }

            this.projectionCache.Add(key, projection);
            this.projectionCacheOrder.Enqueue(key);
            return new ProjectionResolution(projection, snapshot);
        }
    }

    private void InvalidateProjectionCacheIfDefinitionsChanged()
    {
        if (ReferenceEquals(this.cachedWorkDefinitions, catalog.Definitions) &&
            ReferenceEquals(this.cachedWorkflowDefinitions, workflows.Definitions))
        {
            return;
        }

        this.projectionCache.Clear();
        this.projectionCacheOrder.Clear();
        this.cachedWorkDefinitions = catalog.Definitions;
        this.cachedWorkflowDefinitions = workflows.Definitions;
    }

    private bool TryReuseCanonicalSnapshot(
        WorkRequestContext requestContext,
        CanonicalAuthorizationProjection projection)
    {
        if (requestContext.Authorization is not { } snapshot ||
            !this.canonicalSnapshots.TryGetValue(snapshot, out var attestedProjection) ||
            !ReferenceEquals(attestedProjection, projection))
        {
            return false;
        }

        // The weak-table lookup above attests this exact immutable snapshot instance to
        // this projection. Only request-context values that can be replaced while the
        // snapshot reference is retained need to be checked again.
        return snapshot.Actor == requestContext.Actor &&
            snapshot.IsAuthenticated == requestContext.IsAuthenticated;
    }

    private void RegisterCanonicalSnapshot(
        WorkAuthorizationSnapshot snapshot,
        CanonicalAuthorizationProjection projection)
        => this.canonicalSnapshots.GetValue(snapshot, _ => projection);

    private static WorkAuthorizationSnapshot CreateSnapshot(
        WorkActor actor,
        CanonicalAuthorizationProjection projection)
        => new(actor, projection.Groups, projection.ReadFingerprint)
        {
            Scope = projection.Scope,
            IsAuthenticated = projection.IsAuthenticated,
        };

    private static bool GroupsEqual(
        IReadOnlySet<string> first,
        IReadOnlySet<string> second)
        => first.Count == second.Count && first.All(second.Contains);

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
            getCapabilities(),
            getSystemState,
            (_, _) => true,
            sessionDiagnostics,
            new AuthorizedWorkDiscoveryCatalog(catalog),
            sessionCatalog,
            sessionQueue,
            sessionWorkers,
            sessionQuery,
            sessionEvents,
            sessionIterationStatuses,
            sessionChanges);
    }

    private bool CanReconfigureWorker(
        WorkAuthorizationEvaluator authorization,
        WorkRequestContext requestContext,
        WorkerSnapshot worker,
        WorkerReconfiguration changes)
        => catalog.TryGetWork(worker.DefinitionName, out var registeredWork) &&
            authorization.AuthorizeWorkerReconfiguration(
                registeredWork,
                worker,
                changes,
                requestContext).IsAllowed;

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

    private readonly record struct ProjectionResolution(
        CanonicalAuthorizationProjection Projection,
        WorkAuthorizationSnapshot? CreatedSnapshot);

    private sealed record CanonicalAuthorizationProjection(
        IReadOnlySet<string> Groups,
        WorkAuthorizationScope Scope,
        string ReadFingerprint,
        bool IsAuthenticated,
        bool CanViewDiagnostics,
        WorkAuthorizationEvaluator Authorization,
        IReadOnlySet<string> ReadableWorkDefinitionNames,
        IReadOnlySet<string> ReadableWorkflowDefinitionNames);

    private readonly record struct AuthorizationProjectionKey(
        IReadOnlySet<string> Groups,
        bool IsKnownAuthenticatedActor,
        bool IsAuthenticated);

    private sealed class AuthorizationProjectionKeyComparer : IEqualityComparer<AuthorizationProjectionKey>
    {
        internal static AuthorizationProjectionKeyComparer Instance { get; } = new();

        public bool Equals(AuthorizationProjectionKey x, AuthorizationProjectionKey y)
            => x.IsKnownAuthenticatedActor == y.IsKnownAuthenticatedActor &&
                x.IsAuthenticated == y.IsAuthenticated &&
                GroupsEqual(x.Groups, y.Groups);

        public int GetHashCode(AuthorizationProjectionKey obj)
        {
            var hash = HashCode.Combine(
                obj.IsKnownAuthenticatedActor,
                obj.IsAuthenticated,
                obj.Groups.Count);
            foreach (var group in obj.Groups)
            {
                hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(group);
            }

            return hash;
        }
    }
}
