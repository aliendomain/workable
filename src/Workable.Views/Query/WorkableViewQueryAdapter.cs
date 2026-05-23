using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

public class WorkableViewQueryAdapter
{
    private static readonly JsonSerializerOptions ComponentOptionsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly IReadOnlyDictionary<string, WorkComponentDescriptor> ComponentDescriptors =
        new Dictionary<string, WorkComponentDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["throughput"] = new(RequiresIntervalPublish: true),
        };

    public async Task<WorkComponentQueryResult> Components(
        IWorkSystemSession session,
        WorkComponentCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var query = criteria ?? new WorkComponentCriteria();
        var requests = NormalizeComponentRequests(query.Components);
        EnsureAuthorizedComponentAccess(session, requests);
        var queryService = session.Query.BeginRead();
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRequest = NormalizeComponentRequest(request);
            components[request.Id] = await this.CreateComponent(
                session,
                session.Catalog,
                queryService,
                normalizedRequest,
                query.Scope,
                cancellationToken);
        }

        return new WorkComponentQueryResult(DateTimeOffset.UtcNow, components);
    }

    public Task<WorkComponentQueryResult> View(
        IWorkSystemSession session,
        string name,
        WorkViewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var query = criteria ?? new WorkViewCriteria();
        var requests = NormalizeViewComponentRequests(name, query.Components);
        if (requests is null)
        {
            return Task.FromResult(new WorkComponentQueryResult(
                DateTimeOffset.UtcNow,
                new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase)
                {
                    [name] = new("error", Error: $"Unknown view '{name}'."),
                }));
        }

        return this.Components(
            session,
            new WorkComponentCriteria(
                query.Scope,
                requests),
            cancellationToken: cancellationToken);
    }

    public async Task<WorkerSnapshot?> Worker(
        IWorkSystemSession session,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => await session.Query.Worker(workerId, cancellationToken: cancellationToken);

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        IWorkSystemSession session,
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
        => await session.Query.WorkerIteration(iteration, cancellationToken: cancellationToken);

    public Task<WorkerQueryResult> Workers(
        IWorkSystemSession session,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.Workers(criteria, cancellationToken: cancellationToken);

    public Task<WorkerIterationQueryResult> WorkerIterations(
        IWorkSystemSession session,
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerIterations(criteria, cancellationToken: cancellationToken);

    public async Task<WorkInfo?> WorkInfo(
        IWorkSystemSession session,
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
        => await session.Query.WorkInfo(definitionId, cancellationToken: cancellationToken);

    public async Task<WorkInfo?> WorkInfo(
        IWorkSystemSession session,
        string name,
        CancellationToken cancellationToken = default)
        => await session.Query.WorkInfo(name, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<WorkDefinition>> WorkDefinitions(
        IWorkSystemSession session,
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => (await session.Query.WorkDefinitions(criteria, cancellationToken: cancellationToken)).Definitions;

    public Task<WorkerKeyQueryResult> WorkerKeys(
        IWorkSystemSession session,
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerKeys(criteria, cancellationToken: cancellationToken);

    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        IWorkSystemSession session,
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerKeyTypes(criteria, cancellationToken: cancellationToken);

    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        IWorkSystemSession session,
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkIterationKeys(criteria, cancellationToken: cancellationToken);

    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        IWorkSystemSession session,
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkIterationKeyTypes(criteria, cancellationToken: cancellationToken);

    public Task<WorkerStatusSummary> WorkerStatusSummary(
        IWorkSystemSession session,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerStatusSummary(criteria, cancellationToken: cancellationToken);

    public WorkViewCriteria NormalizeViewCriteria(
        string name,
        WorkViewCriteria? criteria = null)
    {
        var query = criteria ?? new WorkViewCriteria();
        var requests = NormalizeViewComponentRequests(name, query.Components);
        return requests is null
            ? query
            : new WorkViewCriteria(query.Scope, [.. requests.Select(NormalizeComponentRequest)]);
    }

    public bool RequiresIntervalPublish(
        string name,
        WorkViewCriteria? criteria = null)
    {
        var query = criteria ?? new WorkViewCriteria();
        var requests = NormalizeViewComponentRequests(name, query.Components);
        return requests is not null &&
            requests
                .Select(NormalizeComponentRequest)
                .Any(ComponentRequiresIntervalPublish);
    }

    public WorkComponentCriteria NormalizeComponentCriteria(WorkComponentCriteria? criteria = null)
    {
        var query = criteria ?? new WorkComponentCriteria();
        return new WorkComponentCriteria(
            query.Scope,
            [.. NormalizeComponentRequests(query.Components).Select(NormalizeComponentRequest)]);
    }

    private async Task<WorkComponentResult> CreateComponent(
        IWorkSystemSession session,
        IWorkCatalog catalog,
        IWorkQueryService queries,
        WorkComponentRequest request,
        WorkSystemCriteria? criteria,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = request.Type.Trim().ToLowerInvariant() switch
            {
                "system" => CreateSystemComponent(session),
                "catalog" => CreateCatalogComponent(catalog, criteria),
                "workers" => await CreateWorkersComponent(queries, criteria, request.Shape, cancellationToken),
                "failedworkers" => await CreateFailedWorkersComponent(queries, criteria, request.Shape, cancellationToken),
                "iterations" => await CreateIterationsComponent(queries, criteria, request.Shape, cancellationToken),
                "failediterations" => await CreateFailedIterationsComponent(queries, criteria, request.Shape, cancellationToken),
                "completediterations" => await CreateCompletedIterationsComponent(queries, criteria, request.Shape, cancellationToken),
                "throughput" => await CreateThroughputComponent(queries, criteria, request.Shape, request.Options, cancellationToken),
                "workergrid" => await CreateWorkerGridComponent(queries, criteria, request.Options, cancellationToken),
                "iterationgrid" => await CreateIterationGridComponent(queries, criteria, request.Options, cancellationToken),
                "systemdiagnostics" => CreateSystemDiagnosticsComponent(session),
                "queuediagnostics" => CreateQueueDiagnosticsComponent(session, request.Shape),
                "readmodeldiagnostics" => CreateReadModelDiagnosticsComponent(session, request.Shape, request.Options),
                "retentiondiagnostics" => CreateRetentionDiagnosticsComponent(session, request.Shape, request.Options),
                "concurrencydiagnostics" => CreateConcurrencyDiagnosticsComponent(session, request.Shape, request.Options),
                "durabilitydiagnostics" => CreateDurabilityDiagnosticsComponent(session, request.Shape, request.Options),
                "idempotencydiagnostics" => CreateIdempotencyDiagnosticsComponent(session, request.Shape),
                _ => null,
            };

            return data is null
                ? new WorkComponentResult("error", Error: $"Unknown component '{request.Type}'.", Shape: request.Shape)
                : new WorkComponentResult("ok", data, Shape: request.Shape);
        }
        catch (Exception exception)
        {
            return new WorkComponentResult("error", Error: exception.Message, Shape: request.Shape);
        }
    }

    private static object CreateSystemComponent(IWorkSystemSession session)
        => new
        {
            SystemName = session.SystemName,
            SystemState = session.SystemState,
        };

    private static object CreateCatalogComponent(IWorkCatalog catalog, WorkSystemCriteria? criteria)
    {
        var level = GetDefinitionCatalogLevel(catalog, criteria?.Category);
        return new
        {
            CatalogCategories = level.Categories,
            CatalogDefinitions = level.Definitions,
        };
    }

    private static WorkDefinitionCatalogLevel GetDefinitionCatalogLevel(
        IWorkCatalog catalog,
        string? category)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        string[] pathSegments = string.IsNullOrWhiteSpace(category)
            ? []
            : SplitCategoryPath(category);
        var categories = new Dictionary<string, WorkSystemCatalogCategoryItem>(StringComparer.OrdinalIgnoreCase);
        var directDefinitions = new List<WorkDefinition>();

        foreach (var definition in catalog.Definitions)
        {
            var definitionSegments = SplitCategoryPath(definition.Category);
            if (!StartsWithCategoryPath(definitionSegments, pathSegments))
            {
                continue;
            }

            var remainingSegments = definitionSegments.Skip(pathSegments.Length).ToArray();
            if (remainingSegments.Length == 0)
            {
                directDefinitions.Add(definition);
                continue;
            }

            var childSegments = pathSegments.Append(remainingSegments[0]).ToArray();
            var childPath = string.Join(':', childSegments);
            categories[childPath] = categories.TryGetValue(childPath, out var existing)
                ? existing with { Count = existing.Count + 1 }
                : new WorkSystemCatalogCategoryItem(
                    remainingSegments[0],
                    childPath,
                    1);
        }

        return new WorkDefinitionCatalogLevel(
            [.. categories.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)],
            [.. directDefinitions
                .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static string[] SplitCategoryPath(string? category)
        => (string.IsNullOrWhiteSpace(category)
                ? WorkDefinitionMetadataDefaults.Category
                : category)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool StartsWithCategoryPath(
        string[] categorySegments,
        string[] pathSegments)
        => pathSegments.Length == 0 ||
            pathSegments.Length <= categorySegments.Length &&
            pathSegments
                .Select((segment, index) => string.Equals(
                    categorySegments[index],
                    segment,
                    StringComparison.OrdinalIgnoreCase))
                .All(matches => matches);

    private static async Task<object> CreateWorkersComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var counts = await queries.SystemWorkerCounts(criteria, cancellationToken: cancellationToken);
        if (shape == WorkComponentShapes.Compact)
        {
            return CreateCompactWorkersComponent(counts);
        }

        return CreateStandardWorkersComponent(counts);
    }

    private static WorkOverviewWorkersCompactComponent CreateCompactWorkersComponent(WorkSystemWorkerCounts counts)
        => new(
            counts.ActiveWorkerCount,
            counts.FailedWorkerCount,
            counts.OldestQueuedAt);

    private static WorkOverviewWorkersStandardComponent CreateStandardWorkersComponent(WorkSystemWorkerCounts counts)
        => new(
            counts.DefinitionCount,
            counts.ActiveWorkerCount,
            counts.FinalWorkerCount,
            counts.FailedWorkerCount,
            counts.WorkerCountByState,
            counts.OldestQueuedAt);

    private static async Task<object> CreateFailedWorkersComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var failedWorkers = await queries.SystemFailedWorkers(criteria, cancellationToken: cancellationToken);
        return shape == WorkComponentShapes.Detailed
            ? failedWorkers.FailedWorkers.Select(worker => new WorkOverviewFailedWorkerDetailed(
                worker.Id,
                worker.DefinitionName,
                worker.Revision,
                worker.State,
                worker.UpdatedAt,
                worker.TotalExecutionDuration,
                worker.SubjectId,
                worker.Identifiers)).ToArray()
            : failedWorkers.FailedWorkers.Select(worker => new WorkOverviewFailedWorkerStandard(
                worker.Id,
                worker.DefinitionName,
                worker.Revision,
                worker.UpdatedAt,
                worker.TotalExecutionDuration)).ToArray();
    }

    private static async Task<WorkViewWorkerGridDetailedComponent> CreateWorkerGridComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var query = CreateWorkerGridCriteria(criteria, options);
        if (!string.IsNullOrWhiteSpace(query.KeyType))
        {
            return await CreateWorkerGridByKeyTypeComponent(queries, criteria, query, cancellationToken);
        }

        var result = await queries.Workers(query.Criteria, cancellationToken: cancellationToken);
        return new WorkViewWorkerGridDetailedComponent(
            result.Workers.Select(CreateWorkerGridDetailed).ToArray(),
            result.TotalCount,
            result.Skip,
            result.Take);
    }

    private static async Task<WorkViewWorkerGridDetailedComponent> CreateWorkerGridByKeyTypeComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? scope,
        WorkViewWorkerGridCriteria query,
        CancellationToken cancellationToken)
    {
        var keyTypes = await queries.WorkerKeyTypes(new WorkerKeyTypeCriteria(
            Type: query.KeyType,
            States: query.Criteria.States), cancellationToken: cancellationToken);
        var workers = keyTypes.Types
            .SelectMany(type => type.Workers)
            .Where(worker => MatchesScope(worker.DefinitionName, worker.Category, scope))
            .DistinctBy(worker => worker.Id)
            .OrderByDescending(worker => worker.UpdatedAt)
            .ToArray();
        var page = workers
            .Skip(query.Criteria.Skip)
            .Take(query.Criteria.Take)
            .Select(CreateWorkerGridDetailed)
            .ToArray();

        return new WorkViewWorkerGridDetailedComponent(
            page,
            workers.Length,
            query.Criteria.Skip,
            query.Criteria.Take);
    }

    private static async Task<object> CreateIterationsComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var counts = await queries.SystemIterationCounts(criteria, cancellationToken: cancellationToken);
        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkOverviewIterationsCompactComponent(
                counts.IterationCountByStatus);
        }

        var keyTypes = await queries.SystemCommonKeyTypes(criteria, cancellationToken: cancellationToken);
        return new WorkOverviewIterationsStandardComponent(
            counts.IterationCountByStatus,
            keyTypes.KeyTypes);
    }

    private static async Task<object> CreateFailedIterationsComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var iterations = await queries.SystemFailedIterations(criteria, cancellationToken: cancellationToken);
        return CreateIterationListComponent(iterations.Iterations, shape);
    }

    private static async Task<object> CreateCompletedIterationsComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var iterations = await queries.SystemCompletedIterations(criteria, cancellationToken: cancellationToken);
        return CreateIterationListComponent(iterations.Iterations, shape);
    }

    private static object CreateIterationListComponent(
        IEnumerable<WorkerIterationOverviewItem> iterations,
        string shape)
        => shape == WorkComponentShapes.Detailed
            ? iterations.Select(iteration => new WorkOverviewIterationDetailed(
                iteration.WorkerId,
                iteration.Sequence,
                iteration.DefinitionName,
                iteration.WorkerState,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                iteration.SubjectId,
                iteration.Identifiers)).ToArray()
            : iterations.Select(iteration => new WorkOverviewIterationStandard(
                iteration.WorkerId,
                iteration.Sequence,
                iteration.DefinitionName,
                iteration.CompletedAt,
                iteration.ExecutionDuration)).ToArray();

    private static async Task<WorkViewIterationGridDetailedComponent> CreateIterationGridComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var query = CreateIterationGridCriteria(criteria, options);
        if (!string.IsNullOrWhiteSpace(query.KeyType))
        {
            return await CreateIterationGridByKeyTypeComponent(queries, criteria, query, cancellationToken);
        }

        var result = await queries.WorkerIterations(query.Criteria, cancellationToken: cancellationToken);
        return new WorkViewIterationGridDetailedComponent(
            result.Iterations.Select(CreateIterationGridDetailed).ToArray(),
            result.TotalCount,
            result.Skip,
            result.Take);
    }

    private static async Task<WorkViewIterationGridDetailedComponent> CreateIterationGridByKeyTypeComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? scope,
        WorkViewIterationGridCriteria query,
        CancellationToken cancellationToken)
    {
        var keyTypes = await queries.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(
            Type: query.KeyType,
            Statuses: query.Criteria.Statuses), cancellationToken: cancellationToken);
        var iterations = keyTypes.Types
            .SelectMany(type => type.Iterations)
            .Where(iteration => MatchesScope(iteration.DefinitionName, iteration.Category, scope))
            .DistinctBy(iteration => new WorkerIterationReference(iteration.WorkerId, iteration.Sequence))
            .OrderByDescending(iteration => iteration.CompletedAt)
            .ToArray();
        var page = iterations
            .Skip(query.Criteria.Skip)
            .Take(query.Criteria.Take)
            .Select(CreateIterationGridDetailed)
            .ToArray();

        return new WorkViewIterationGridDetailedComponent(
            page,
            iterations.Length,
            query.Criteria.Skip,
            query.Criteria.Take);
    }

    private static async Task<object> CreateThroughputComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? criteria,
        string shape,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var workerCounts = await queries.SystemWorkerCounts(criteria, cancellationToken: cancellationToken);
        var throughputCriteria = CreateThroughputCriteria(options);
        if (shape == WorkComponentShapes.Compact)
        {
            var summary = await queries.SystemThroughputSummary(
                criteria,
                throughputCriteria,
                cancellationToken: cancellationToken);
            return new WorkOverviewThroughputCompactComponent(
                workerCounts.ActiveWorkerCount,
                new WorkOverviewThroughputCompact(
                    summary.WindowSeconds,
                    summary.SettledCount,
                    summary.ExecutionSummary,
                    CreateLiveSummary(summary.LiveSummary)));
        }

        var throughput = await queries.SystemThroughput(
            criteria,
            throughputCriteria,
            cancellationToken: cancellationToken);
        return new WorkOverviewThroughputStandardComponent(
            workerCounts.ActiveWorkerCount,
            new WorkOverviewThroughputStandard(
                throughput.From,
                throughput.To,
                throughput.WindowSeconds,
                throughput.BucketSeconds,
                throughput.SettledCount,
                throughput.Buckets.Select(bucket => new WorkOverviewThroughputBucket(
                    bucket.At,
                    bucket.Started,
                    bucket.Completed,
                    bucket.Failed,
                    bucket.Canceled,
                    bucket.AverageExecutionMilliseconds)).ToArray(),
                throughput.ExecutionSummary,
                CreateLiveSummary(throughput.LiveSummary)));
    }

    private static WorkOverviewThroughputLiveSummary CreateLiveSummary(WorkThroughputLiveSummary summary)
        => new(
            summary.WindowSeconds,
            summary.StartedPerSecond,
            summary.CompletedPerSecond,
            summary.FailedPerSecond,
            summary.CanceledPerSecond,
            summary.InFlightDeltaPerSecond);

    private static object CreateQueueDiagnosticsComponent(
        IWorkSystemSession session,
        string shape)
    {
        var queue = session.Diagnostics.Queue;
        var hasRejectedWork = queue.RejectedWorkCount > 0;
        var hasAlertableRejectedWork = queue.AlertableRejectedWorkCount > 0;

        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkQueueDiagnosticsCompactComponent(
                queue.RejectedWorkCount,
                hasRejectedWork,
                queue.LastRejectedAt,
                queue.LastRejectedCode,
                queue.LastRejectedMessage,
                queue.AlertableRejectedWorkCount,
                hasAlertableRejectedWork,
                queue.LastAlertableRejectedCode,
                queue.LastAlertableRejectedMessage);
        }

        return new WorkQueueDiagnosticsDetailedComponent(
            queue,
            hasRejectedWork);
    }

    private static object CreateSystemDiagnosticsComponent(IWorkSystemSession session)
        => new WorkSystemDiagnosticsCompactComponent(
            session.SystemName,
            session.SystemState,
            session.SystemState == WorkSystemState.Stopping);

    private static object CreateReadModelDiagnosticsComponent(
        IWorkSystemSession session,
        string shape,
        JsonElement? options)
    {
        var readModel = session.Diagnostics.ReadModel;
        var warningThreshold = Math.Max(1, TryGetInt32(options, "warningThreshold") ?? 100);
        var isBehind = readModel.PendingUpdateCount >= warningThreshold;

        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkReadModelDiagnosticsCompactComponent(
                readModel.PendingUpdateCount,
                isBehind,
                warningThreshold,
                readModel.HasProjectorFailure,
                readModel.ProjectorFailureType,
                readModel.ProjectorFailureMessage);
        }

        return new WorkReadModelDiagnosticsDetailedComponent(
            readModel,
            isBehind,
            warningThreshold);
    }

    private static object CreateRetentionDiagnosticsComponent(
        IWorkSystemSession session,
        string shape,
        JsonElement? options)
    {
        var retention = session.Diagnostics.Retention;
        var warningSeconds = Math.Max(1, TryGetInt32(options, "warningSeconds") ?? 30);
        var isBehind = retention.OldestDuePurgeAge >= TimeSpan.FromSeconds(warningSeconds);

        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkRetentionDiagnosticsCompactComponent(
                retention.TrackedFinalWorkerCount,
                retention.ScheduledPurgeCount,
                retention.OldestDuePurgeAge,
                isBehind,
                warningSeconds,
                retention.HasSchedulerFailure,
                retention.SchedulerFailureType,
                retention.SchedulerFailureMessage);
        }

        return new WorkRetentionDiagnosticsDetailedComponent(
            retention,
            isBehind,
            warningSeconds);
    }

    private static object CreateConcurrencyDiagnosticsComponent(
        IWorkSystemSession session,
        string shape,
        JsonElement? options)
    {
        var concurrency = session.Diagnostics.Concurrency;
        var warningSeconds = Math.Max(1, TryGetInt32(options, "warningSeconds") ?? 30);
        var isBehind = concurrency.DeferredStartCount > 0 &&
            concurrency.OldestDeferredStartAge >= TimeSpan.FromSeconds(warningSeconds);

        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkConcurrencyDiagnosticsCompactComponent(
                concurrency.DeferredStartCount,
                concurrency.OldestDeferredStartAge,
                concurrency.LastDrainReleasedCount,
                isBehind,
                warningSeconds);
        }

        return new WorkConcurrencyDiagnosticsDetailedComponent(
            concurrency,
            isBehind,
            warningSeconds);
    }

    private static object CreateDurabilityDiagnosticsComponent(
        IWorkSystemSession session,
        string shape,
        JsonElement? options)
    {
        var durability = session.Diagnostics.Durability;
        var acceptedWorkerWarningSeconds = Math.Max(1, TryGetInt32(options, "acceptedWorkerWarningSeconds") ?? 30);
        var cleanupWarningSeconds = Math.Max(1, TryGetInt32(options, "cleanupWarningSeconds") ?? 30);
        var isAcceptedWorkerMaterializationBehind = durability.AcceptedWaiterCount > 0 &&
            durability.OldestAcceptedWaiterAge >= TimeSpan.FromSeconds(acceptedWorkerWarningSeconds);
        var isCleanupBehind = durability.PendingCleanupCount > 0 &&
            durability.OldestPendingCleanupAge >= TimeSpan.FromSeconds(cleanupWarningSeconds);

        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkDurabilityDiagnosticsCompactComponent(
                durability.AcceptedWaiterCount,
                durability.OldestAcceptedWaiterAge,
                durability.PendingCleanupCount,
                durability.OldestPendingCleanupAge,
                isAcceptedWorkerMaterializationBehind,
                acceptedWorkerWarningSeconds,
                isCleanupBehind,
                cleanupWarningSeconds,
                durability.HasReaderFailure,
                durability.ReaderFailureType,
                durability.ReaderFailureMessage,
                durability.HasLeaseRenewalFailure,
                durability.LeaseRenewalFailureType,
                durability.LeaseRenewalFailureMessage,
                durability.HasCleanupFailure,
                durability.CleanupFailureType,
                durability.CleanupFailureMessage);
        }

        return new WorkDurabilityDiagnosticsDetailedComponent(
            durability,
            isAcceptedWorkerMaterializationBehind,
            acceptedWorkerWarningSeconds,
            isCleanupBehind,
            cleanupWarningSeconds);
    }

    private static object CreateIdempotencyDiagnosticsComponent(
        IWorkSystemSession session,
        string shape)
    {
        var idempotency = session.Diagnostics.Idempotency;
        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkIdempotencyDiagnosticsCompactComponent(
                idempotency.DuplicateRejectionCount,
                idempotency.LastDuplicateRejectedStorage?.ToString());
        }

        return new WorkIdempotencyDiagnosticsDetailedComponent(idempotency);
    }

    private static WorkViewWorkerGridDetailed CreateWorkerGridDetailed(WorkerOverviewItem worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.Revision,
            worker.State,
            worker.UpdatedAt,
            worker.TotalExecutionDuration,
            worker.SubjectId,
            worker.Identifiers);

    private static WorkViewIterationGridDetailed CreateIterationGridDetailed(WorkerIterationOverviewItem iteration)
        => new(
            iteration.WorkerId,
            iteration.Sequence,
            iteration.DefinitionName,
            iteration.WorkerState,
            iteration.Status,
            iteration.CompletedAt,
            iteration.ExecutionDuration,
            iteration.SubjectId,
            iteration.Identifiers);

    private static IReadOnlyList<WorkComponentRequest>? NormalizeViewComponentRequests(
        string name,
        IReadOnlyList<WorkComponentRequest>? requests)
    {
        if (string.Equals(name, "overview", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeComponentRequests(requests);
        }

        if (string.Equals(name, "workers", StringComparison.OrdinalIgnoreCase))
        {
            return requests is { Count: > 0 }
                ? requests
                : [new("workerGrid", "workerGrid", Shape: WorkComponentShapes.Detailed)];
        }

        if (string.Equals(name, "iterations", StringComparison.OrdinalIgnoreCase))
        {
            return requests is { Count: > 0 }
                ? requests
                : [new("iterationGrid", "iterationGrid", Shape: WorkComponentShapes.Detailed)];
        }

        if (string.Equals(name, "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            return requests is { Count: > 0 }
                ? requests
                : [
                    new("queueDiagnostics", "queueDiagnostics", Shape: WorkComponentShapes.Compact),
                    new("readModelDiagnostics", "readModelDiagnostics", Shape: WorkComponentShapes.Compact),
                    new("retentionDiagnostics", "retentionDiagnostics", Shape: WorkComponentShapes.Compact),
                    new("concurrencyDiagnostics", "concurrencyDiagnostics", Shape: WorkComponentShapes.Compact),
                    new("durabilityDiagnostics", "durabilityDiagnostics", Shape: WorkComponentShapes.Compact),
                    new("idempotencyDiagnostics", "idempotencyDiagnostics", Shape: WorkComponentShapes.Compact),
                ];
        }

        return null;
    }

    private static IReadOnlyList<WorkComponentRequest> NormalizeComponentRequests(
        IReadOnlyList<WorkComponentRequest>? requests)
        => requests is { Count: > 0 }
            ? requests
            : [
                new("system", "system"),
                new("workers", "workers", Shape: WorkComponentShapes.Standard),
                new("failedWorkers", "failedWorkers", Shape: WorkComponentShapes.Standard),
                new("iterations", "iterations"),
                new("failedIterations", "failedIterations", Shape: WorkComponentShapes.Standard),
                new("completedIterations", "completedIterations", Shape: WorkComponentShapes.Standard),
            ];

    private static WorkComponentRequest NormalizeComponentRequest(WorkComponentRequest request)
    {
        var shape = NormalizeComponentShape(request.Shape);
        if (string.Equals(request.Type, "workers", StringComparison.OrdinalIgnoreCase) &&
            shape == WorkComponentShapes.Detailed)
        {
            shape = WorkComponentShapes.Standard;
        }
        else if (string.Equals(request.Type, "throughput", StringComparison.OrdinalIgnoreCase) &&
            shape == WorkComponentShapes.Detailed)
        {
            shape = WorkComponentShapes.Standard;
        }
        else if (string.Equals(request.Type, "failedWorkers", StringComparison.OrdinalIgnoreCase) &&
            shape == WorkComponentShapes.Compact)
        {
            shape = WorkComponentShapes.Standard;
        }
        else if ((string.Equals(request.Type, "workerGrid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "iterationGrid", StringComparison.OrdinalIgnoreCase)) &&
            shape != WorkComponentShapes.Detailed)
        {
            shape = WorkComponentShapes.Detailed;
        }
        else if (string.Equals(request.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase) &&
            shape == WorkComponentShapes.Standard)
        {
            shape = WorkComponentShapes.Detailed;
        }
        else if (string.Equals(request.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase) &&
            shape == WorkComponentShapes.Standard)
        {
            shape = WorkComponentShapes.Detailed;
        }
        else if ((string.Equals(request.Type, "concurrencyDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "durabilityDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "idempotencyDiagnostics", StringComparison.OrdinalIgnoreCase)) &&
            shape == WorkComponentShapes.Standard)
        {
            shape = WorkComponentShapes.Detailed;
        }
        else if (string.Equals(request.Type, "queueDiagnostics", StringComparison.OrdinalIgnoreCase) &&
            shape == WorkComponentShapes.Standard)
        {
            shape = WorkComponentShapes.Detailed;
        }

        return request with { Shape = shape };
    }

    private static bool ComponentRequiresIntervalPublish(WorkComponentRequest request)
        => ComponentDescriptors.TryGetValue(request.Type.Trim(), out var descriptor) &&
            descriptor.RequiresIntervalPublish;

    private static void EnsureAuthorizedComponentAccess(
        IWorkSystemSession session,
        IReadOnlyList<WorkComponentRequest> requests)
    {
        foreach (var request in requests)
        {
            if (!IsDiagnosticsComponent(request))
            {
                continue;
            }

            // Diagnostics access is all-or-nothing at the system boundary, so touching any
            // diagnostics facet is enough to trigger the authorization guard for this session.
            _ = session.Diagnostics.Queue;
            return;
        }
    }

    private static bool IsDiagnosticsComponent(WorkComponentRequest request)
        => string.Equals(request.Type, "systemDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "queueDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "concurrencyDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "durabilityDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Type, "idempotencyDiagnostics", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeComponentShape(string? shape)
    {
        if (string.IsNullOrWhiteSpace(shape))
        {
            return WorkComponentShapes.Detailed;
        }

        return shape.Trim().ToLowerInvariant() switch
        {
            WorkComponentShapes.Compact => WorkComponentShapes.Compact,
            WorkComponentShapes.Standard => WorkComponentShapes.Standard,
            WorkComponentShapes.Detailed => WorkComponentShapes.Detailed,
            var unknown => unknown,
        };
    }

    private static WorkThroughputCriteria? CreateThroughputCriteria(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var windowSeconds = TryGetInt32(options.Value, "windowSeconds") ??
            WorkThroughputCriteria.DefaultWindowSeconds;
        var bucketSeconds = TryGetInt32(options.Value, "bucketSeconds") ??
            WorkThroughputCriteria.DefaultBucketSeconds;
        return new WorkThroughputCriteria(windowSeconds, bucketSeconds);
    }

    private static WorkViewWorkerGridCriteria CreateWorkerGridCriteria(
        WorkSystemCriteria? scope,
        JsonElement? options)
    {
        var query = DeserializeOptions<WorkViewWorkerGridOptions>(options) ?? new WorkViewWorkerGridOptions();
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, WorkerCriteria.MaximumTake);
        return new WorkViewWorkerGridCriteria(
            new WorkerCriteria(
                DefinitionId: scope?.DefinitionId,
                DefinitionName: scope?.DefinitionName,
                States: query.States?.ToHashSet(),
                Configuration: query.Configuration,
                Sort: WorkerCriteriaSort.UpdatedAt,
                Direction: WorkCriteriaSortDirection.Descending,
                Skip: skip,
                Take: take,
                Category: scope?.Category,
                IncludeSubcategories: scope?.IncludeSubcategories ?? true),
            query.KeyType);
    }

    private static WorkViewIterationGridCriteria CreateIterationGridCriteria(
        WorkSystemCriteria? scope,
        JsonElement? options)
    {
        var query = DeserializeOptions<WorkViewIterationGridOptions>(options) ?? new WorkViewIterationGridOptions();
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, WorkerIterationCriteria.MaximumTake);
        return new WorkViewIterationGridCriteria(
            new WorkerIterationCriteria(
                DefinitionId: scope?.DefinitionId,
                DefinitionName: scope?.DefinitionName,
                Category: scope?.Category,
                Statuses: query.Statuses?.ToHashSet(),
                Sort: WorkerIterationCriteriaSort.CompletedAt,
                Direction: WorkCriteriaSortDirection.Descending,
                Skip: skip,
                Take: take),
            query.KeyType);
    }

    private static T? DeserializeOptions<T>(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        return options.Value.Deserialize<T>(ComponentOptionsJson);
    }

    private static bool MatchesScope(
        string definitionName,
        string category,
        WorkSystemCriteria? scope)
    {
        if (scope is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(scope.DefinitionName) &&
            !string.Equals(definitionName, scope.DefinitionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(scope.Category))
        {
            return true;
        }

        return scope.IncludeSubcategories
            ? category.Equals(scope.Category, StringComparison.OrdinalIgnoreCase) ||
                category.StartsWith($"{scope.Category}:", StringComparison.OrdinalIgnoreCase)
            : category.Equals(scope.Category, StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryGetInt32(JsonElement options, string propertyName)
        => options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value)
                ? value
                : null;

    private static int? TryGetInt32(JsonElement? options, string propertyName)
        => options.HasValue && options.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? TryGetInt32(options.Value, propertyName)
            : null;

    private sealed record WorkViewWorkerGridCriteria(
        WorkerCriteria Criteria,
        string? KeyType);

    private sealed record WorkViewIterationGridCriteria(
        WorkerIterationCriteria Criteria,
        string? KeyType);

    private sealed record WorkViewWorkerGridOptions(
        string? KeyType = null,
        IReadOnlyList<WorkerState>? States = null,
        WorkerConfigurationCriteria? Configuration = null,
        int Skip = 0,
        int Take = WorkerCriteria.DefaultTake);

    private sealed record WorkViewIterationGridOptions(
        string? KeyType = null,
        IReadOnlyList<WorkCompletionStatus>? Statuses = null,
        int Skip = 0,
        int Take = WorkerIterationCriteria.DefaultTake);

    private sealed record WorkDefinitionCatalogLevel(
        IReadOnlyList<WorkSystemCatalogCategoryItem> Categories,
        IReadOnlyList<WorkDefinition> Definitions);

    private sealed record WorkComponentDescriptor(
        bool RequiresIntervalPublish = false);
}
