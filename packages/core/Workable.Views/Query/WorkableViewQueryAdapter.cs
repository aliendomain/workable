using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Projects Workable query data into the shared view/component and worker-overview contracts.
/// </summary>
public class WorkableViewQueryAdapter
{
    private const int InitialWorkerOverviewRealtimeActivityTake = 50;
    private readonly record struct WorkerOverviewLogRecord(long Sequence, WorkerLogEntry Entry);

    private static readonly JsonSerializerOptions ComponentOptionsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly IReadOnlyDictionary<string, WorkComponentDescriptor> ComponentDescriptors =
        new Dictionary<string, WorkComponentDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["throughput"] = new(RequiresIntervalPublish: true),
        };

    /// <summary>
    /// Builds a component result map from an arbitrary component request list.
    /// </summary>
    public async Task<WorkComponentQueryResult> Components(
        IWorkSystemSession session,
        WorkComponentCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var query = criteria ?? new WorkComponentCriteria();
        var requests = NormalizeComponentRequests(query.Components);
        EnsureAuthorizedComponentAccess(session, requests);
        var queryService = session.Query;
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

    /// <summary>
    /// Builds a named view using the built-in default component composition or caller-supplied overrides.
    /// </summary>
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

    /// <summary>
    /// Reads one raw worker snapshot for custom transports or custom UI flows.
    /// </summary>
    public async Task<WorkerSnapshot?> Worker(
        IWorkSystemSession session,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => await session.Query.Worker(workerId, cancellationToken: cancellationToken);

    /// <summary>
    /// Reads one raw worker-iteration snapshot for custom transports or custom UI flows.
    /// </summary>
    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        IWorkSystemSession session,
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
        => await session.Query.WorkerIteration(iteration, cancellationToken: cancellationToken);

    /// <summary>
    /// Builds the paged structured-message section for one worker iteration.
    /// </summary>
    public async Task<WorkIterationMessageSection?> WorkerIterationMessages(
        IWorkSystemSession session,
        WorkerIterationReference iteration,
        WorkIterationMessageCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var snapshot = await session.Query.WorkerIteration(iteration, cancellationToken: cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var query = NormalizeIterationMessageCriteria(criteria);
        var filteredMessages = SortIterationMessages(
            FilterIterationMessages(snapshot.Messages, query.Severities),
            query.SortDirection);

        return new WorkIterationMessageSection(
            CreateIterationMessageSummary(snapshot.Messages),
            CreateIterationMessagePage(filteredMessages, query.Cursor, query.Take));
    }

    /// <summary>
    /// Builds the paged log section for one worker iteration.
    /// </summary>
    public async Task<WorkIterationLogSection?> WorkerIterationLogs(
        IWorkSystemSession session,
        WorkerIterationReference iteration,
        WorkIterationLogCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var snapshot = await session.Query.WorkerIteration(iteration, cancellationToken: cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var query = NormalizeIterationLogCriteria(criteria);
        var filteredLogs = SortIterationLogs(
            FilterIterationLogs(snapshot.Logs, query.Levels),
            query.SortDirection);

        return new WorkIterationLogSection(
            CreateIterationLogSummary(snapshot.Logs),
            CreateIterationLogPage(filteredLogs, query.Cursor, query.Take));
    }

    /// <summary>
    /// Queries workers for grid-style UI surfaces.
    /// </summary>
    public Task<WorkerQueryResult> Workers(
        IWorkSystemSession session,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.Workers(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Queries worker iterations for grid-style UI surfaces.
    /// </summary>
    public Task<WorkerIterationQueryResult> WorkerIterations(
        IWorkSystemSession session,
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerIterations(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Reads one definition summary from the caller-scoped catalog.
    /// </summary>
    public async Task<WorkInfo?> WorkInfo(
        IWorkSystemSession session,
        string name,
        CancellationToken cancellationToken = default)
        => await session.Query.WorkInfo(name, cancellationToken: cancellationToken);

    /// <summary>
    /// Builds the HTTP landing payload for one worker detail screen.
    /// </summary>
    public async Task<WorkWorkerOverviewComponent?> WorkerOverview(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkWorkerOverviewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
        if (worker is null)
        {
            return null;
        }

        var query = NormalizeWorkerOverviewCriteria(criteria);
        session.Catalog.TryGet(worker.DefinitionName, out var definition);
        var activity = ResolveWorkerOverviewActivity(worker, query.Activity);
        var mergedIterations = worker.GetMergedIterations();
        var latestIteration = mergedIterations.FirstOrDefault();
        var recentIterations = mergedIterations
            .OrderByDescending(iteration => iteration.Sequence)
            .Take(query.RecentIterationTake)
            .Select(iteration => new WorkWorkerOverviewRecentIteration(
                worker.Id,
                iteration.Sequence,
                iteration.Status,
                iteration.StartedAt,
                iteration.SettledAt,
                iteration.SettledExecutionDuration,
                iteration.AttemptCount))
            .ToArray();
        var allLogEntries = CreateWorkerOverviewLogRecords(mergedIterations, query.LogIterationSequence);
        var filteredLogEntries = SortWorkerOverviewLogEntries(
            FilterWorkerOverviewLogEntries(allLogEntries, query.LogLevels),
            query.LogSortDirection);
        var baseTimelineItems = worker.GetActivityEvents(mergedIterations)
            .Select(CreateWorkerOverviewTimelineItem)
            .ToArray();
        var allTimelineItems = ApplyWorkerOverviewRetryPending(
            worker,
            latestIteration,
            AddWorkerOverviewLiveStateItems(worker, latestIteration, baseTimelineItems));
        var filteredTimelineItems = SortWorkerOverviewTimelineItems(
            FilterWorkerOverviewTimelineItems(allTimelineItems, query.TimelineCategories),
            query.TimelineSortDirection);

        return new WorkWorkerOverviewComponent(
            activity,
            new WorkWorkerOverviewWorker(
                worker.Id,
                worker.Revision,
                worker.StateSequence,
                worker.State,
                worker.IsFinal,
                worker.CreatedAt,
                worker.StateChangedAt,
                worker.UpdatedAt,
                worker.NextRunAt,
                worker.RetryAttempt,
                CreateWorkerOverviewOrigin(worker.Origin),
                worker.DefinitionName,
                worker.DefinitionCategory,
                CountWorkerOverviewConfigurationDifferences(worker, definition)),
            worker.Input,
            latestIteration is null
                ? null
                : CreateWorkerOverviewLatestIteration(worker, latestIteration, includeOutput: true),
            recentIterations,
            new WorkWorkerOverviewLogSection(
                CreateWorkerOverviewLogSummary(allLogEntries),
                activity == WorkWorkerOverviewActivity.Logs
                    ? CreateWorkerOverviewLogPage(filteredLogEntries, query.ActivityCursor, query.ActivityTake)
                    : null),
            new WorkWorkerOverviewTimelineSection(
                CreateWorkerOverviewTimelineSummary(allTimelineItems),
                activity == WorkWorkerOverviewActivity.Timeline
                    ? CreateWorkerOverviewTimelinePage(filteredTimelineItems, query.ActivityCursor, query.ActivityTake)
                    : null));
    }

    /// <summary>
    /// Builds only the worker-log section for one worker detail screen.
    /// </summary>
    public async Task<WorkWorkerOverviewLogSection?> WorkerOverviewLogs(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkWorkerOverviewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
        if (worker is null)
        {
            return null;
        }

        var query = NormalizeWorkerOverviewCriteria(criteria);
        var mergedIterations = worker.GetMergedIterations();
        var allLogEntries = CreateWorkerOverviewLogRecords(mergedIterations, query.LogIterationSequence);
        var filteredLogEntries = SortWorkerOverviewLogEntries(
            FilterWorkerOverviewLogEntries(allLogEntries, query.LogLevels),
            query.LogSortDirection);

        return new WorkWorkerOverviewLogSection(
            CreateWorkerOverviewLogSummary(allLogEntries),
            CreateWorkerOverviewLogPage(filteredLogEntries, query.ActivityCursor, query.ActivityTake));
    }

    /// <summary>
    /// Builds only the worker-timeline section for one worker detail screen.
    /// </summary>
    public async Task<WorkWorkerOverviewTimelineSection?> WorkerOverviewTimeline(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkWorkerOverviewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
        if (worker is null)
        {
            return null;
        }

        var query = NormalizeWorkerOverviewCriteria(criteria);
        var mergedIterations = worker.GetMergedIterations();
        var latestIteration = mergedIterations.FirstOrDefault();
        var baseTimelineItems = worker.GetActivityEvents(mergedIterations)
            .Select(CreateWorkerOverviewTimelineItem)
            .ToArray();
        var allTimelineItems = ApplyWorkerOverviewRetryPending(
            worker,
            latestIteration,
            AddWorkerOverviewLiveStateItems(worker, latestIteration, baseTimelineItems));
        var filteredTimelineItems = SortWorkerOverviewTimelineItems(
            FilterWorkerOverviewTimelineItems(allTimelineItems, query.TimelineCategories),
            query.TimelineSortDirection);

        return new WorkWorkerOverviewTimelineSection(
            CreateWorkerOverviewTimelineSummary(allTimelineItems),
            CreateWorkerOverviewTimelinePage(filteredTimelineItems, query.ActivityCursor, query.ActivityTake));
    }

    /// <summary>
    /// Builds the full realtime seed state for a worker-overview SignalR subscription.
    /// </summary>
    public async Task<WorkWorkerOverviewRealtimeState?> WorkerOverviewRealtimeState(
        IWorkSystemSession session,
        WorkerId workerId,
        WorkWorkerOverviewRealtimeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
        if (worker is null)
        {
            return null;
        }

        var query = NormalizeWorkerOverviewRealtimeCriteria(criteria);
        session.Catalog.TryGet(worker.DefinitionName, out var definition);
        var mergedIterations = worker.GetMergedIterations();
        var latestIteration = mergedIterations.FirstOrDefault();
        var recentIterations = IsExpandedRealtimeShape(query.WorkerDuration)
            ? mergedIterations
                .OrderByDescending(iteration => iteration.Sequence)
                .Take(25)
                .Select(iteration => new WorkWorkerOverviewRecentIteration(
                    worker.Id,
                    iteration.Sequence,
                    iteration.Status,
                    iteration.StartedAt,
                    iteration.SettledAt,
                    iteration.SettledExecutionDuration,
                    iteration.AttemptCount))
                .ToArray()
            : [];
        var allLogEntries = CreateWorkerOverviewLogRecords(mergedIterations, query.LogIterationSequence);
        var filteredLogEntries = SortWorkerOverviewLogEntries(
            FilterWorkerOverviewLogEntries(allLogEntries, query.LogLevels),
            query.LogSortDirection);
        var baseTimelineItems = worker.GetActivityEvents(mergedIterations)
            .Select(CreateWorkerOverviewTimelineItem)
            .ToArray();
        var allTimelineItems = ApplyWorkerOverviewRetryPending(
            worker,
            latestIteration,
            AddWorkerOverviewLiveStateItems(worker, latestIteration, baseTimelineItems));
        var filteredTimelineItems = SortWorkerOverviewTimelineItems(
            FilterWorkerOverviewTimelineItems(allTimelineItems, query.TimelineCategories),
            query.TimelineSortDirection)
            .Where(item => item.Kind == WorkWorkerOverviewTimelineItemKind.Iteration)
            .ToArray();

        return new WorkWorkerOverviewRealtimeState(
            new WorkWorkerOverviewWorker(
                worker.Id,
                worker.Revision,
                worker.StateSequence,
                worker.State,
                worker.IsFinal,
                worker.CreatedAt,
                worker.StateChangedAt,
                worker.UpdatedAt,
                worker.NextRunAt,
                worker.RetryAttempt,
                CreateWorkerOverviewOrigin(worker.Origin),
                worker.DefinitionName,
                worker.DefinitionCategory,
                CountWorkerOverviewConfigurationDifferences(worker, definition)),
            latestIteration is null
                ? null
                : CreateWorkerOverviewLatestIteration(
                    worker,
                    latestIteration,
                    includeOutput: string.Equals(query.WorkerControls, WorkComponentShapes.Standard, StringComparison.Ordinal)),
            IncludesLogSummary(query.WorkerLogs)
                ? CreateWorkerOverviewLogSummary(allLogEntries)
                : null,
            IsExpandedRealtimeShape(query.WorkerLogs)
                ? filteredLogEntries
                    .Take(InitialWorkerOverviewRealtimeActivityTake)
                    .Select(record => new WorkWorkerOverviewLogEntry(
                        record.Entry.Id.ToString("N"),
                        record.Entry.OccurredAt,
                        record.Entry.Level,
                        record.Entry.Category,
                        record.Entry.Message,
                        record.Entry.EventId.Id,
                        record.Entry.EventId.Name,
                        record.Entry.ExceptionType,
                        record.Entry.ExceptionMessage,
                        record.Sequence,
                        record.Entry.Ordinal))
                    .ToArray()
                : [],
            recentIterations,
            IsExpandedRealtimeShape(query.WorkerTimeline)
                ? CreateWorkerOverviewTimelineSummary(allTimelineItems)
                : null,
            IsExpandedRealtimeShape(query.WorkerTimeline)
                ? filteredTimelineItems.Take(InitialWorkerOverviewRealtimeActivityTake).ToArray()
                : []);
    }

    /// <summary>
    /// Queries caller-visible work definitions.
    /// </summary>
    public async Task<IReadOnlyList<WorkDefinition>> WorkDefinitions(
        IWorkSystemSession session,
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => (await session.Query.WorkDefinitions(criteria, cancellationToken: cancellationToken)).Definitions;

    /// <summary>
    /// Queries worker keys for scope-building and filter UIs.
    /// </summary>
    public Task<WorkerKeyQueryResult> WorkerKeys(
        IWorkSystemSession session,
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerKeys(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Queries worker key-type facets for scope-building and filter UIs.
    /// </summary>
    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        IWorkSystemSession session,
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerKeyTypes(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Queries iteration keys for scope-building and filter UIs.
    /// </summary>
    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        IWorkSystemSession session,
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkIterationKeys(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Queries iteration key-type facets for scope-building and filter UIs.
    /// </summary>
    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        IWorkSystemSession session,
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkIterationKeyTypes(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Queries worker status counts for summary widgets.
    /// </summary>
    public Task<WorkerStatusSummary> WorkerStatusSummary(
        IWorkSystemSession session,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => session.Query.WorkerStatusSummary(criteria, cancellationToken: cancellationToken);

    /// <summary>
    /// Normalizes a named view request into the canonical shape used by the adapter and realtime grouping.
    /// </summary>
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

    /// <summary>
    /// Determines whether a named view contains any components that require interval-based publishing.
    /// </summary>
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

    /// <summary>
    /// Normalizes a component query into the canonical shape used by the adapter and realtime grouping.
    /// </summary>
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
                "workerdetail" => await CreateWorkerDetailComponent(queries, request.Options, cancellationToken),
                "workercurrentiteration" => await CreateWorkerCurrentIterationComponent(queries, request.Options, cancellationToken),
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
        return await CreateGridComponent(
            query.Criteria,
            criteria,
            query.KeyKind,
            query.KeyType,
            query.KeyValue,
            async (workerCriteria, token) =>
            {
                var result = await queries.Workers(workerCriteria, cancellationToken: token);
                return new WorkViewWorkerGridDetailedComponent(
                    result.Workers.Select(CreateWorkerGridDetailed).ToArray(),
                    result.TotalCount,
                    result.Skip,
                    result.Take);
            },
            (_, token) => CreateWorkerGridByKeyFilterComponent(queries, criteria, query, token),
            cancellationToken);
    }

    private static async Task<WorkViewWorkerGridDetailedComponent> CreateWorkerGridByKeyFilterComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? scope,
        WorkViewWorkerGridCriteria query,
        CancellationToken cancellationToken)
    {
        return await CreateKeyFilterGridComponent(
            scope,
            query.KeyValue,
            query.Criteria.Skip,
            query.Criteria.Take,
            async (token) => (await queries.WorkerKeys(new WorkerKeyCriteria(
                Kind: query.KeyKind,
                Type: query.KeyType,
                Value: query.KeyValue,
                States: query.Criteria.States), cancellationToken: token)).Keys,
            async (token) => (await queries.WorkerKeyTypes(new WorkerKeyTypeCriteria(
                Kind: query.KeyKind,
                Type: query.KeyType,
                States: query.Criteria.States), cancellationToken: token)).Types,
            key => key.Workers,
            keyType => keyType.Workers,
            worker => worker.DefinitionName,
            worker => worker.Category,
            worker => worker.Id,
            worker => worker.UpdatedAt,
            CreateWorkerGridDetailed,
            (page, totalCount, skip, take) => new WorkViewWorkerGridDetailedComponent(page, totalCount, skip, take),
            cancellationToken);
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
        return await CreateGridComponent(
            query.Criteria,
            criteria,
            query.KeyKind,
            query.KeyType,
            query.KeyValue,
            async (iterationCriteria, token) =>
            {
                var result = await queries.WorkerIterations(iterationCriteria, cancellationToken: token);
                return new WorkViewIterationGridDetailedComponent(
                    result.Iterations.Select(CreateIterationGridDetailed).ToArray(),
                    result.TotalCount,
                    result.Skip,
                    result.Take);
            },
            (_, token) => CreateIterationGridByKeyFilterComponent(queries, criteria, query, token),
            cancellationToken);
    }

    private static async Task<WorkViewIterationGridDetailedComponent> CreateIterationGridByKeyFilterComponent(
        IWorkQueryService queries,
        WorkSystemCriteria? scope,
        WorkViewIterationGridCriteria query,
        CancellationToken cancellationToken)
    {
        return await CreateKeyFilterGridComponent(
            scope,
            query.KeyValue,
            query.Criteria.Skip,
            query.Criteria.Take,
            async (token) => (await queries.WorkIterationKeys(new WorkIterationKeyCriteria(
                Kind: query.KeyKind,
                Type: query.KeyType,
                Value: query.KeyValue,
                Statuses: query.Criteria.Statuses), cancellationToken: token)).Keys,
            async (token) => (await queries.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(
                Kind: query.KeyKind,
                Type: query.KeyType,
                Statuses: query.Criteria.Statuses), cancellationToken: token)).Types,
            key => key.Iterations,
            keyType => keyType.Iterations,
            iteration => iteration.DefinitionName,
            iteration => iteration.Category,
            iteration => new WorkerIterationReference(iteration.WorkerId, iteration.Sequence),
            iteration => iteration.CompletedAt,
            CreateIterationGridDetailed,
            (page, totalCount, skip, take) => new WorkViewIterationGridDetailedComponent(page, totalCount, skip, take),
            cancellationToken);
    }

    private static async Task<WorkerSnapshot?> CreateWorkerDetailComponent(
        IWorkQueryService queries,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var workerId = GetRequiredWorkerId(options);
        return await queries.Worker(workerId, cancellationToken: cancellationToken);
    }

    private static async Task<WorkerIterationSnapshot?> CreateWorkerCurrentIterationComponent(
        IWorkQueryService queries,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var workerId = GetRequiredWorkerId(options);
        var worker = await queries.Worker(workerId, cancellationToken: cancellationToken);
        if (worker?.CurrentIterationSequence is not long sequence)
        {
            return null;
        }

        return await queries.WorkerIteration(
            new WorkerIterationReference(workerId, sequence),
            cancellationToken: cancellationToken);
    }

    private static WorkWorkerOverviewCriteria NormalizeWorkerOverviewCriteria(WorkWorkerOverviewCriteria? criteria)
    {
        var query = criteria ?? new WorkWorkerOverviewCriteria();
        return query with
        {
            ActivityTake = Math.Clamp(query.ActivityTake, 1, 100),
            RecentIterationTake = Math.Clamp(query.RecentIterationTake, 1, 25),
            LogLevels = NormalizeWorkerOverviewEnumFilters(query.LogLevels),
            LogIterationSequence = query.LogIterationSequence is > 0 ? query.LogIterationSequence : null,
            TimelineCategories = NormalizeWorkerOverviewEnumFilters(query.TimelineCategories),
        };
    }

    /// <summary>
    /// Normalizes worker-overview realtime criteria into the canonical shape used by the adapter and SignalR grouping.
    /// </summary>
    public WorkWorkerOverviewRealtimeCriteria NormalizeWorkerOverviewRealtimeCriteria(
        WorkWorkerOverviewRealtimeCriteria? criteria = null)
    {
        var query = criteria ?? new WorkWorkerOverviewRealtimeCriteria();
        return query with
        {
            WorkerControls = NormalizeRealtimeWorkerOverviewShape(query.WorkerControls, WorkComponentShapes.Compact),
            WorkerLogs = NormalizeRealtimeWorkerOverviewShape(query.WorkerLogs, WorkComponentShapes.Compact),
            WorkerDuration = NormalizeRealtimeWorkerOverviewShape(query.WorkerDuration, WorkComponentShapes.Compact),
            WorkerTimeline = NormalizeRealtimeWorkerOverviewShape(query.WorkerTimeline, WorkComponentShapes.Compact),
            LogLevels = NormalizeWorkerOverviewEnumFilters(query.LogLevels),
            LogIterationSequence = query.LogIterationSequence is > 0 ? query.LogIterationSequence : null,
            TimelineCategories = NormalizeWorkerOverviewEnumFilters(query.TimelineCategories),
        };
    }

    private static IReadOnlyList<TEnum>? NormalizeWorkerOverviewEnumFilters<TEnum>(IReadOnlyList<TEnum>? values)
        where TEnum : struct, Enum
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var normalized = values
            .Distinct()
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static WorkWorkerOverviewActivity ResolveWorkerOverviewActivity(
        WorkerSnapshot worker,
        WorkWorkerOverviewActivity requestedActivity)
        => requestedActivity != WorkWorkerOverviewActivity.Auto
            ? requestedActivity
            : worker.State is WorkerState.Completed or WorkerState.Canceled ||
                worker.Configuration.Recurrence.IsEnabled
                ? WorkWorkerOverviewActivity.Timeline
                : WorkWorkerOverviewActivity.Logs;

    private static string NormalizeRealtimeWorkerOverviewShape(string? shape, string fallback)
    {
        var normalized = NormalizeComponentShape(shape);
        return normalized is WorkComponentShapes.Standard or WorkComponentShapes.Detailed
            ? normalized
            : fallback;
    }

    private static bool IsExpandedRealtimeShape(string shape)
        => string.Equals(shape, WorkComponentShapes.Standard, StringComparison.Ordinal) ||
            string.Equals(shape, WorkComponentShapes.Detailed, StringComparison.Ordinal);

    private static bool IncludesLogSummary(string shape)
        => string.Equals(shape, WorkComponentShapes.Compact, StringComparison.Ordinal) ||
            IsExpandedRealtimeShape(shape);

    private static WorkWorkerOverviewLatestIteration CreateWorkerOverviewLatestIteration(
        WorkerSnapshot worker,
        WorkerIterationSnapshot latestIteration,
        bool includeOutput)
        => new(
            worker.Id,
            latestIteration.Sequence,
            latestIteration.Status,
            latestIteration.StartedAt,
            latestIteration.SettledAt,
            latestIteration.SettledExecutionDuration,
            includeOutput ? latestIteration.Output : null,
            CreateWorkerOverviewFailure(
                latestIteration.Failure,
                CreateWorkerOverviewRetryPendingState(worker, latestIteration)),
            latestIteration.AttemptCount);

    private static WorkWorkerOverviewLogSummary CreateWorkerOverviewLogSummary(
        IReadOnlyList<WorkerOverviewLogRecord> entries)
        => new(
            entries.Count,
            entries.Count(entry => entry.Entry.Level == LogLevel.Critical),
            entries.Count(entry => entry.Entry.Level == LogLevel.Error),
            entries.Count(entry => entry.Entry.Level is LogLevel.Error or LogLevel.Critical),
            entries.Count(entry => entry.Entry.Level == LogLevel.Warning),
            entries.Count(entry => entry.Entry.Level == LogLevel.Warning),
            entries.Count(entry => entry.Entry.Level == LogLevel.Information),
            entries.Count(entry => entry.Entry.Level == LogLevel.Debug),
            entries.Count(entry => entry.Entry.Level == LogLevel.Trace));

    private static IReadOnlyList<WorkerOverviewLogRecord> CreateWorkerOverviewLogRecords(
        IReadOnlyList<WorkerIterationSnapshot> iterations,
        long? sequence = null)
        => [.. iterations
            .Where(iteration => !sequence.HasValue || iteration.Sequence == sequence.Value)
            .SelectMany(iteration => iteration.Logs.Select(entry => new WorkerOverviewLogRecord(iteration.Sequence, entry)))
            .GroupBy(record => record.Entry.Id)
            .Select(group => group
                .OrderByDescending(record => record.Entry.OccurredAt)
                .ThenByDescending(record => record.Sequence)
                .First())
            .OrderByDescending(record => record.Entry.OccurredAt)
            .ThenByDescending(record => record.Sequence)
            .ThenByDescending(record => record.Entry.Ordinal)
            .ThenByDescending(GetWorkerOverviewLogEntryId)];

    private static IReadOnlyList<WorkerOverviewLogRecord> FilterWorkerOverviewLogEntries(
        IReadOnlyList<WorkerOverviewLogRecord> entries,
        IReadOnlyList<LogLevel>? levels)
    {
        if (levels is null || levels.Count == 0)
        {
            return entries;
        }

        var allowedLevels = levels.ToHashSet();
        return entries
            .Where(entry => allowedLevels.Contains(entry.Entry.Level))
            .ToArray();
    }

    private static IReadOnlyList<WorkerOverviewLogRecord> SortWorkerOverviewLogEntries(
        IReadOnlyList<WorkerOverviewLogRecord> entries,
        WorkWorkerOverviewSortDirection direction)
        => direction == WorkWorkerOverviewSortDirection.Asc
            ? entries
                .OrderBy(entry => entry.Entry.OccurredAt)
                .ThenBy(entry => entry.Sequence)
                .ThenBy(entry => entry.Entry.Ordinal)
                .ThenBy(GetWorkerOverviewLogEntryId, StringComparer.Ordinal)
                .ToArray()
            : entries
                .OrderByDescending(entry => entry.Entry.OccurredAt)
                .ThenByDescending(entry => entry.Sequence)
                .ThenByDescending(entry => entry.Entry.Ordinal)
                .ThenByDescending(GetWorkerOverviewLogEntryId, StringComparer.Ordinal)
                .ToArray();

    private static WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry> CreateWorkerOverviewLogPage(
        IReadOnlyList<WorkerOverviewLogRecord> entries,
        string? cursor,
        int take)
    {
        var pageEntries = SliceWorkerOverviewPage(entries, GetWorkerOverviewLogEntryId, cursor, take);
        var items = pageEntries.Items
            .Take(take)
            .Select(record => new WorkWorkerOverviewLogEntry(
                record.Entry.Id.ToString("N"),
                record.Entry.OccurredAt,
                record.Entry.Level,
                record.Entry.Category,
                record.Entry.Message,
                record.Entry.EventId.Id,
                record.Entry.EventId.Name,
                record.Entry.ExceptionType,
                record.Entry.ExceptionMessage,
                record.Sequence,
                record.Entry.Ordinal))
            .ToArray();

        return new WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry>(
            items,
            pageEntries.HasMore,
            items.LastOrDefault()?.Id);
    }

    private static string GetWorkerOverviewLogEntryId(WorkerOverviewLogRecord entry)
        => entry.Entry.Id.ToString("N");

    private static WorkWorkerOverviewTimelineItem CreateWorkerOverviewTimelineItem(WorkerActivityEvent item)
        => new(
            item.Id,
            item.At,
            item.Kind switch
            {
                WorkerActivityEventKind.ActionRequest => WorkWorkerOverviewTimelineItemKind.ActionRequest,
                WorkerActivityEventKind.StateChange => WorkWorkerOverviewTimelineItemKind.StateChange,
                WorkerActivityEventKind.Iteration => WorkWorkerOverviewTimelineItemKind.Iteration,
                _ => throw new InvalidOperationException($"Unknown worker activity event kind '{item.Kind}'."),
            },
            item.Category switch
            {
                WorkerActivityEventCategory.UserAction => WorkWorkerOverviewTimelineCategory.UserAction,
                WorkerActivityEventCategory.SystemEvent => WorkWorkerOverviewTimelineCategory.SystemEvent,
                WorkerActivityEventCategory.Failure => WorkWorkerOverviewTimelineCategory.Failure,
                _ => throw new InvalidOperationException($"Unknown worker activity event category '{item.Category}'."),
            },
            item.ActionHistoryKind,
            item.Action,
            item.ActionStatus,
            item.State,
            item.Sequence,
            item.IterationStatus,
            item.ExecutionDuration,
            item.Origin is null ? null : CreateWorkerOverviewOrigin(item.Origin),
            CreateWorkerOverviewFailure(item.Failure),
            AttemptCount: item.AttemptCount);

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem> AddWorkerOverviewLiveStateItems(
        WorkerSnapshot worker,
        WorkerIterationSnapshot? latestIteration,
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items)
    {
        var liveStateItem = CreateWorkerOverviewLiveStateItem(worker, latestIteration, items);
        return liveStateItem is null
            ? items
            : [liveStateItem, .. items];
    }

    private static WorkWorkerOverviewTimelineItem? CreateWorkerOverviewLiveStateItem(
        WorkerSnapshot worker,
        WorkerIterationSnapshot? latestIteration,
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items)
    {
        var latestIterationStatus = latestIteration?.Status;
        var hasMatchingStateItem = (WorkerState state) => items.Any(item =>
            item.Kind == WorkWorkerOverviewTimelineItemKind.StateChange &&
            item.State == state);

        return worker.State switch
        {
            WorkerState.Paused when latestIterationStatus != WorkCompletionStatus.Paused &&
                !hasMatchingStateItem(WorkerState.Paused) => CreateWorkerOverviewLiveStateItem(
                    worker,
                    WorkerState.Paused),
            WorkerState.Canceled when latestIterationStatus != WorkCompletionStatus.Canceled &&
                !hasMatchingStateItem(WorkerState.Canceled) => CreateWorkerOverviewLiveStateItem(
                    worker,
                    WorkerState.Canceled),
            WorkerState.Waiting when !hasMatchingStateItem(WorkerState.Waiting) => CreateWorkerOverviewLiveStateItem(
                    worker,
                    WorkerState.Waiting,
                    CreateWorkerOverviewPendingState(
                        WorkWorkerOverviewPendingStateMode.Recurrence,
                        worker)),
            _ => null,
        };
    }

    private static WorkWorkerOverviewTimelineItem CreateWorkerOverviewLiveStateItem(
        WorkerSnapshot worker,
        WorkerState state,
        WorkWorkerOverviewPendingState? pendingState = null)
        => new(
            Id: CreateWorkerOverviewLiveStateItemId(worker, state),
            At: worker.StateChangedAt,
            Kind: WorkWorkerOverviewTimelineItemKind.StateChange,
            Category: WorkWorkerOverviewTimelineCategory.SystemEvent,
            ActionHistoryKind: null,
            Action: null,
            ActionStatus: null,
            State: state,
            Sequence: null,
            IterationStatus: null,
            AttemptCount: null,
            ExecutionDuration: null,
            Origin: null,
            Failure: null,
            PendingState: pendingState);

    private static string CreateWorkerOverviewLiveStateItemId(WorkerSnapshot worker, WorkerState state)
        => state == WorkerState.Waiting
            ? "live-state:waiting"
            : $"state:{state.ToString().ToLowerInvariant()}:{worker.StateSequence}";

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem> ApplyWorkerOverviewRetryPending(
        WorkerSnapshot worker,
        WorkerIterationSnapshot? latestIteration,
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items)
    {
        var retryPending = CreateWorkerOverviewRetryPendingState(worker, latestIteration);
        if (retryPending is null)
        {
            return items;
        }

        return [.. items.Select(item =>
            item.Kind == WorkWorkerOverviewTimelineItemKind.Iteration &&
            item.Sequence == latestIteration?.Sequence &&
            item.Failure is not null
                ? item with
                {
                    Failure = item.Failure with
                    {
                        PendingState = retryPending,
                    },
                }
                : item)];
    }

    private static WorkWorkerOverviewTimelineSummary CreateWorkerOverviewTimelineSummary(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items)
        => new(
            items.Count,
            items.Count(item => item.Category == WorkWorkerOverviewTimelineCategory.UserAction),
            items.Count(item => item.Category == WorkWorkerOverviewTimelineCategory.SystemEvent),
            items.Count(item => item.Category == WorkWorkerOverviewTimelineCategory.Failure));

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem> FilterWorkerOverviewTimelineItems(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items,
        IReadOnlyList<WorkWorkerOverviewTimelineCategory>? categories)
    {
        var uniqueItems = items
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        if (categories is null || categories.Count == 0)
        {
            return uniqueItems;
        }

        var allowedCategories = categories.ToHashSet();
        return uniqueItems
            .Where(item => allowedCategories.Contains(item.Category))
            .ToArray();
    }

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem> SortWorkerOverviewTimelineItems(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items,
        WorkWorkerOverviewSortDirection direction)
        => direction == WorkWorkerOverviewSortDirection.Asc
            ? items
                .OrderBy(item => item.At)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray()
            : items
                .OrderByDescending(item => item.At)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();

    private static WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem> CreateWorkerOverviewTimelinePage(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items,
        string? cursor,
        int take)
    {
        var page = SliceWorkerOverviewPage(items, item => item.Id, cursor, take);
        var pageItems = page.Items.ToArray();
        return new WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem>(
            pageItems,
            page.HasMore,
            pageItems.LastOrDefault()?.Id);
    }

    private static (IReadOnlyList<T> Items, bool HasMore) SliceWorkerOverviewPage<T>(
        IReadOnlyList<T> items,
        Func<T, string> getCursor,
        string? cursor,
        int take)
    {
        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorIndex = items
                .Select((item, index) => (Cursor: getCursor(item), Index: index))
                .FirstOrDefault(entry => string.Equals(entry.Cursor, cursor, StringComparison.Ordinal));
            if (cursorIndex.Cursor is not null)
            {
                startIndex = cursorIndex.Index + 1;
            }
        }

        var remaining = items.Skip(startIndex).ToArray();
        return (remaining.Take(take).ToArray(), remaining.Length > take);
    }

    private static WorkIterationMessageCriteria NormalizeIterationMessageCriteria(
        WorkIterationMessageCriteria? criteria)
    {
        var query = criteria ?? new WorkIterationMessageCriteria();
        return query with
        {
            Take = Math.Clamp(query.Take, 1, 200),
        };
    }

    private static WorkIterationLogCriteria NormalizeIterationLogCriteria(
        WorkIterationLogCriteria? criteria)
    {
        var query = criteria ?? new WorkIterationLogCriteria();
        return query with
        {
            Take = Math.Clamp(query.Take, 1, 200),
        };
    }

    private static IReadOnlyList<WorkMessage> FilterIterationMessages(
        IReadOnlyList<WorkMessage> messages,
        IReadOnlyList<WorkMessageSeverity>? severities)
    {
        if (severities is not { Count: > 0 })
        {
            return messages;
        }

        var allowed = severities.ToHashSet();
        return messages
            .Where(message => allowed.Contains(message.Severity))
            .ToArray();
    }

    private static IReadOnlyList<WorkMessage> SortIterationMessages(
        IReadOnlyList<WorkMessage> messages,
        WorkWorkerOverviewSortDirection direction)
        => direction == WorkWorkerOverviewSortDirection.Asc
            ? messages
                .Select((message, index) => (Message: message, Index: index))
                .OrderBy(entry => entry.Message.OccurredAt)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Message)
                .ToArray()
            : messages
                .Select((message, index) => (Message: message, Index: index))
                .OrderByDescending(entry => entry.Message.OccurredAt)
                .ThenByDescending(entry => entry.Index)
                .Select(entry => entry.Message)
                .ToArray();

    private static WorkIterationMessageSummary CreateIterationMessageSummary(
        IReadOnlyList<WorkMessage> messages)
        => new(
            messages.Count,
            messages.Count(message => message.Severity == WorkMessageSeverity.Critical),
            messages.Count(message => message.Severity == WorkMessageSeverity.Error),
            messages.Count(message => message.Severity == WorkMessageSeverity.Error),
            messages.Count(message => message.Severity == WorkMessageSeverity.Warning),
            messages.Count(message => message.Severity == WorkMessageSeverity.Warning),
            messages.Count(message => message.Severity == WorkMessageSeverity.Information),
            messages.Count(message => message.Severity == WorkMessageSeverity.Debug),
            messages.Count(message => message.Severity == WorkMessageSeverity.Trace));

    private static WorkIterationMessagePage CreateIterationMessagePage(
        IReadOnlyList<WorkMessage> items,
        string? cursor,
        int take)
    {
        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(cursor) &&
            int.TryParse(cursor, out var parsed) &&
            parsed >= 0)
        {
            startIndex = Math.Min(parsed, items.Count);
        }

        var remaining = items.Skip(startIndex).ToArray();
        var pageItems = remaining.Take(take).ToArray();
        var hasMore = remaining.Length > take;
        return new WorkIterationMessagePage(
            pageItems,
            hasMore,
            hasMore ? (startIndex + pageItems.Length).ToString() : null);
    }

    private static IReadOnlyList<WorkerLogEntry> FilterIterationLogs(
        IReadOnlyList<WorkerLogEntry> logs,
        IReadOnlyList<LogLevel>? levels)
    {
        if (levels is not { Count: > 0 })
        {
            return logs;
        }

        var allowed = levels.ToHashSet();
        return logs
            .Where(entry => allowed.Contains(entry.Level))
            .ToArray();
    }

    private static IReadOnlyList<WorkerLogEntry> SortIterationLogs(
        IReadOnlyList<WorkerLogEntry> logs,
        WorkWorkerOverviewSortDirection direction)
        => direction == WorkWorkerOverviewSortDirection.Asc
            ? logs
                .OrderBy(entry => entry.OccurredAt)
                .ThenBy(entry => entry.Ordinal)
                .ThenBy(entry => entry.Id)
                .ToArray()
            : logs
                .OrderByDescending(entry => entry.OccurredAt)
                .ThenByDescending(entry => entry.Ordinal)
                .ThenByDescending(entry => entry.Id)
                .ToArray();

    private static WorkWorkerOverviewLogSummary CreateIterationLogSummary(
        IReadOnlyList<WorkerLogEntry> logs)
        => new(
            logs.Count,
            logs.Count(entry => entry.Level == LogLevel.Critical),
            logs.Count(entry => entry.Level == LogLevel.Error),
            logs.Count(entry => entry.Level is LogLevel.Critical or LogLevel.Error),
            logs.Count(entry => entry.Level == LogLevel.Warning),
            logs.Count(entry => entry.Level == LogLevel.Warning),
            logs.Count(entry => entry.Level == LogLevel.Information),
            logs.Count(entry => entry.Level == LogLevel.Debug),
            logs.Count(entry => entry.Level == LogLevel.Trace));

    private static WorkWorkerOverviewPage<WorkerLogEntry> CreateIterationLogPage(
        IReadOnlyList<WorkerLogEntry> items,
        string? cursor,
        int take)
    {
        var page = SliceWorkerOverviewPage(items, item => item.Id.ToString("N"), cursor, take);
        var pageItems = page.Items.ToArray();
        return new WorkWorkerOverviewPage<WorkerLogEntry>(
            pageItems,
            page.HasMore,
            pageItems.LastOrDefault()?.Id.ToString("N"));
    }

    private static int CountWorkerOverviewConfigurationDifferences(
        WorkerSnapshot worker,
        WorkDefinition? definition)
    {
        if (definition is null)
        {
            return 0;
        }

        return WorkerConfigurationDifferenceCounter.CountDifferences(
            worker.Options,
            worker.Configuration,
            new WorkerOptions(definition.DefaultOptions.ProfilingEnabled),
            definition.Configuration);
    }

    private static WorkWorkerOverviewOrigin CreateWorkerOverviewOrigin(WorkOrigin origin)
        => new(
            origin.Channel,
            origin.Surface,
            NormalizeWorkerOverviewText(origin.Actor.Id),
            NormalizeWorkerOverviewText(origin.Actor.Name),
            NormalizeWorkerOverviewText(origin.Actor.Email));

    private static WorkWorkerOverviewFailure? CreateWorkerOverviewFailure(
        WorkerIterationFailure? failure,
        WorkWorkerOverviewPendingState? pendingState = null)
        => failure is null
            ? null
            : new WorkWorkerOverviewFailure(
                failure.Kind switch
                {
                    WorkerIterationFailureKind.Failure => WorkWorkerOverviewFailureKind.Failure,
                    WorkerIterationFailureKind.Exception => WorkWorkerOverviewFailureKind.Exception,
                    _ => throw new InvalidOperationException($"Unknown worker iteration failure kind '{failure.Kind}'."),
                },
                failure.Message,
                failure.Code,
                failure.Target,
                failure.ExceptionType,
                failure.StackTrace,
                failure.DeclaredByWork,
                pendingState);

    private static WorkWorkerOverviewPendingState? CreateWorkerOverviewRetryPendingState(
        WorkerSnapshot worker,
        WorkerIterationSnapshot? latestIteration)
        => worker.State == WorkerState.Retrying && latestIteration?.Failure is not null
            ? CreateWorkerOverviewPendingState(WorkWorkerOverviewPendingStateMode.Retry, worker)
            : null;

    private static WorkWorkerOverviewPendingState CreateWorkerOverviewPendingState(
        WorkWorkerOverviewPendingStateMode mode,
        WorkerSnapshot worker)
        => new(
            mode,
            worker.NextRunAt,
            worker.StateChangedAt,
            worker.UpdatedAt,
            worker.RetryAttempt);

    private static string? NormalizeWorkerOverviewText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

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
            worker.IsFinal,
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
            iteration.IsFinal,
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

        if (string.Equals(name, "worker", StringComparison.OrdinalIgnoreCase))
        {
            return requests is { Count: > 0 }
                ? requests
                : [
                    new("worker", "workerDetail", Shape: WorkComponentShapes.Detailed),
                    new("currentIteration", "workerCurrentIteration", Shape: WorkComponentShapes.Detailed),
                ];
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
        var keyKind = query.KeyKind;
        var keyType = NormalizeQueryText(query.KeyType);
        var keyValue = NormalizeQueryText(query.KeyValue);
        return new WorkViewWorkerGridCriteria(
            ApplyExactWorkerKeyCriteria(
                new WorkerCriteria(
                    DefinitionName: scope?.DefinitionName,
                    DefinitionNames: scope?.DefinitionNames,
                    States: query.States?.ToHashSet(),
                    Configuration: query.Configuration,
                    Sort: WorkerCriteriaSort.UpdatedAt,
                    Direction: WorkCriteriaSortDirection.Descending,
                    Skip: skip,
                    Take: take,
                    Category: scope?.Category,
                    IncludeSubcategories: scope?.IncludeSubcategories ?? true),
                keyKind,
                keyType,
                keyValue),
            keyKind,
            keyType,
            keyValue);
    }

    private static WorkViewIterationGridCriteria CreateIterationGridCriteria(
        WorkSystemCriteria? scope,
        JsonElement? options)
    {
        var query = DeserializeOptions<WorkViewIterationGridOptions>(options) ?? new WorkViewIterationGridOptions();
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, WorkerIterationCriteria.MaximumTake);
        var keyKind = query.KeyKind;
        var keyType = NormalizeQueryText(query.KeyType);
        var keyValue = NormalizeQueryText(query.KeyValue);
        return new WorkViewIterationGridCriteria(
            ApplyExactIterationKeyCriteria(
                new WorkerIterationCriteria(
                    DefinitionName: scope?.DefinitionName,
                    DefinitionNames: scope?.DefinitionNames,
                    Category: scope?.Category,
                    Statuses: query.Statuses?.ToHashSet(),
                    Sort: WorkerIterationCriteriaSort.CompletedAt,
                    Direction: WorkCriteriaSortDirection.Descending,
                    Skip: skip,
                    Take: take),
                keyKind,
                keyType,
                keyValue),
            keyKind,
            keyType,
            keyValue);
    }

    private static bool HasKeyFilter(
        WorkKeyKind? keyKind,
        string? keyType,
        string? keyValue)
        => keyKind is not null ||
            !string.IsNullOrWhiteSpace(keyType) ||
            !string.IsNullOrWhiteSpace(keyValue);

    private static bool HasExactStructuredKey(
        WorkKeyKind? keyKind,
        string? keyType,
        string? keyValue)
        => keyKind is not null &&
            !string.IsNullOrWhiteSpace(keyType) &&
            !string.IsNullOrWhiteSpace(keyValue);

    private static string? NormalizeQueryText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static async Task<TComponent> CreateGridComponent<TCriteria, TComponent>(
        TCriteria criteria,
        WorkSystemCriteria? scope,
        WorkKeyKind? keyKind,
        string? keyType,
        string? keyValue,
        Func<TCriteria, CancellationToken, Task<TComponent>> loadExact,
        Func<WorkSystemCriteria?, CancellationToken, Task<TComponent>> loadKeyFiltered,
        CancellationToken cancellationToken)
        where TCriteria : notnull
    {
        if (HasKeyFilter(keyKind, keyType, keyValue))
        {
            if (HasExactStructuredKey(keyKind, keyType, keyValue))
            {
                return await loadExact(criteria, cancellationToken);
            }

            return await loadKeyFiltered(scope, cancellationToken);
        }

        return await loadExact(criteria, cancellationToken);
    }

    private static async Task<TComponent> CreateKeyFilterGridComponent<TSource, TKeyDescriptor, TKeyTypeDescriptor, TIdentity, TRow, TComponent>(
        WorkSystemCriteria? scope,
        string? keyValue,
        int skip,
        int take,
        Func<CancellationToken, Task<IReadOnlyList<TKeyDescriptor>>> loadKeys,
        Func<CancellationToken, Task<IReadOnlyList<TKeyTypeDescriptor>>> loadKeyTypes,
        Func<TKeyDescriptor, IEnumerable<TSource>> keyItems,
        Func<TKeyTypeDescriptor, IEnumerable<TSource>> keyTypeItems,
        Func<TSource, string> definitionName,
        Func<TSource, string> category,
        Func<TSource, TIdentity> identity,
        Func<TSource, DateTimeOffset> sortKey,
        Func<TSource, TRow> map,
        Func<TRow[], int, int, int, TComponent> create,
        CancellationToken cancellationToken)
        where TIdentity : notnull
    {
        IEnumerable<TSource> items = !string.IsNullOrWhiteSpace(keyValue)
            ? (await loadKeys(cancellationToken)).SelectMany(keyItems)
            : (await loadKeyTypes(cancellationToken)).SelectMany(keyTypeItems);

        var scoped = items
            .Where(item => MatchesScope(definitionName(item), category(item), scope))
            .DistinctBy(identity)
            .OrderByDescending(sortKey)
            .ToArray();
        var page = scoped
            .Skip(skip)
            .Take(take)
            .Select(map)
            .ToArray();

        return create(page, scoped.Length, skip, take);
    }

    private static WorkerCriteria ApplyExactWorkerKeyCriteria(
        WorkerCriteria criteria,
        WorkKeyKind? keyKind,
        string? keyType,
        string? keyValue)
        => ApplyExactStructuredKey(
            criteria,
            keyKind,
            keyType,
            keyValue,
            (current, subjectId) => current with { SubjectId = subjectId },
            (current, concurrencyKey) => current with { ConcurrencyKey = concurrencyKey },
            (current, identifier) => current with { Identifier = identifier });

    private static WorkerIterationCriteria ApplyExactIterationKeyCriteria(
        WorkerIterationCriteria criteria,
        WorkKeyKind? keyKind,
        string? keyType,
        string? keyValue)
        => ApplyExactStructuredKey(
            criteria,
            keyKind,
            keyType,
            keyValue,
            (current, subjectId) => current with { SubjectId = subjectId },
            (current, concurrencyKey) => current with { ConcurrencyKey = concurrencyKey },
            (current, identifier) => current with { Identifier = identifier });

    private static TCriteria ApplyExactStructuredKey<TCriteria>(
        TCriteria criteria,
        WorkKeyKind? keyKind,
        string? keyType,
        string? keyValue,
        Func<TCriteria, WorkSubjectId, TCriteria> withSubjectId,
        Func<TCriteria, WorkConcurrencyKey, TCriteria> withConcurrencyKey,
        Func<TCriteria, WorkIdentifier, TCriteria> withIdentifier)
        where TCriteria : notnull
    {
        if (!HasExactStructuredKey(keyKind, keyType, keyValue))
        {
            return criteria;
        }

        return keyKind switch
        {
            WorkKeyKind.Subject => withSubjectId(criteria, new WorkSubjectId(keyType!, keyValue!)),
            WorkKeyKind.ConcurrencyKey => withConcurrencyKey(criteria, new WorkConcurrencyKey(keyType!, keyValue!)),
            WorkKeyKind.Identifier => withIdentifier(criteria, new WorkIdentifier(keyType!, keyValue!)),
            _ => criteria,
        };
    }

    private static WorkerId GetRequiredWorkerId(JsonElement? options)
    {
        var query = DeserializeOptions<WorkViewWorkerOptions>(options);
        if (string.IsNullOrWhiteSpace(query?.WorkerId))
        {
            throw new InvalidOperationException("workerId is required.");
        }

        return Guid.TryParse(query.WorkerId, out var workerId)
            ? new WorkerId(workerId)
            : throw new InvalidOperationException("workerId must be a valid GUID.");
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
        WorkKeyKind? KeyKind,
        string? KeyType,
        string? KeyValue);

    private sealed record WorkViewIterationGridCriteria(
        WorkerIterationCriteria Criteria,
        WorkKeyKind? KeyKind,
        string? KeyType,
        string? KeyValue);

    private sealed record WorkViewWorkerOptions(
        string? WorkerId = null);

    private sealed record WorkViewWorkerGridOptions(
        WorkKeyKind? KeyKind = null,
        string? KeyType = null,
        string? KeyValue = null,
        IReadOnlyList<WorkerState>? States = null,
        WorkerConfigurationCriteria? Configuration = null,
        int Skip = 0,
        int Take = WorkerCriteria.DefaultTake);

    private sealed record WorkViewIterationGridOptions(
        WorkKeyKind? KeyKind = null,
        string? KeyType = null,
        string? KeyValue = null,
        IReadOnlyList<WorkCompletionStatus>? Statuses = null,
        int Skip = 0,
        int Take = WorkerIterationCriteria.DefaultTake);

    private sealed record WorkDefinitionCatalogLevel(
        IReadOnlyList<WorkSystemCatalogCategoryItem> Categories,
        IReadOnlyList<WorkDefinition> Definitions);

    private sealed record WorkComponentDescriptor(
        bool RequiresIntervalPublish = false);
}
