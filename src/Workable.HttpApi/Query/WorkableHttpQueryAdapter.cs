using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

public sealed class WorkableHttpQueryAdapter
{
    private static readonly JsonSerializerOptions ComponentOptionsJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<WorkComponentQueryResult> Components(
        IWorkSystem system,
        WorkComponentCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        var query = criteria ?? new WorkComponentCriteria();
        var requests = NormalizeComponentRequests(query.Components);
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRequest = NormalizeComponentRequest(request);
            components[request.Id] = await this.CreateComponent(system, normalizedRequest, query.Scope, cancellationToken);
        }

        return new WorkComponentQueryResult(DateTimeOffset.UtcNow, components);
    }

    public Task<WorkComponentQueryResult> View(
        IWorkSystem system,
        string name,
        WorkViewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

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
            system,
            new WorkComponentCriteria(
                query.Scope,
                requests),
            cancellationToken: cancellationToken);
    }

    public async Task<WorkerSnapshot?> Worker(
        IWorkSystem system,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.Worker(workerId, cancellationToken: cancellationToken);
    }

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        IWorkSystem system,
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.WorkerIteration(iteration, cancellationToken: cancellationToken);
    }

    public Task<WorkerQueryResult> Workers(
        IWorkSystem system,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.Workers(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkerIterationQueryResult> WorkerIterations(
        IWorkSystem system,
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerIterations(criteria, cancellationToken: cancellationToken);
    }

    public async Task<WorkInfo?> WorkInfo(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.WorkInfo(definitionId, cancellationToken: cancellationToken);
    }

    public async Task<WorkInfo?> WorkInfo(
        IWorkSystem system,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.WorkInfo(name, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WorkDefinition>> WorkDefinitions(
        IWorkSystem system,
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return (await system.Query.WorkDefinitions(criteria, cancellationToken: cancellationToken)).Definitions;
    }

    public Task<WorkerKeyQueryResult> WorkerKeys(
        IWorkSystem system,
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerKeys(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        IWorkSystem system,
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerKeyTypes(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        IWorkSystem system,
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkIterationKeys(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        IWorkSystem system,
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkIterationKeyTypes(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkerStatusSummary> WorkerStatusSummary(
        IWorkSystem system,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerStatusSummary(criteria, cancellationToken: cancellationToken);
    }

    private async Task<WorkComponentResult> CreateComponent(
        IWorkSystem system,
        WorkComponentRequest request,
        WorkSystemCriteria? criteria,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = request.Type.Trim().ToLowerInvariant() switch
            {
                "system" => CreateSystemComponent(system),
                "catalog" => CreateCatalogComponent(system, criteria),
                "workers" => await CreateWorkersComponent(system, criteria, request.Shape, cancellationToken),
                "failedworkers" => await CreateFailedWorkersComponent(system, criteria, request.Shape, cancellationToken),
                "iterations" => await CreateIterationsComponent(system, criteria, request.Shape, cancellationToken),
                "failediterations" => await CreateFailedIterationsComponent(system, criteria, request.Shape, cancellationToken),
                "completediterations" => await CreateCompletedIterationsComponent(system, criteria, request.Shape, cancellationToken),
                "throughput" => await CreateThroughputComponent(system, criteria, request.Shape, request.Options, cancellationToken),
                "workergrid" => await CreateWorkerGridComponent(system, criteria, request.Options, cancellationToken),
                "iterationgrid" => await CreateIterationGridComponent(system, criteria, request.Options, cancellationToken),
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

    private static object CreateSystemComponent(IWorkSystem system)
        => new
        {
            SystemName = system.Name,
            SystemState = system.State,
        };

    private static object CreateCatalogComponent(IWorkSystem system, WorkSystemCriteria? criteria)
    {
        var level = WorkableHttpCatalogAdapter.GetDefinitionCatalogLevel(system, criteria?.Category);
        return new
        {
            CatalogCategories = level.Categories,
            CatalogDefinitions = level.Definitions,
        };
    }

    private static async Task<object> CreateWorkersComponent(
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var counts = await system.Query.SystemWorkerCounts(criteria, cancellationToken: cancellationToken);
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
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var failedWorkers = await system.Query.SystemFailedWorkers(criteria, cancellationToken: cancellationToken);
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
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var query = CreateWorkerGridCriteria(criteria, options);
        if (!string.IsNullOrWhiteSpace(query.KeyType))
        {
            return await CreateWorkerGridByKeyTypeComponent(system, criteria, query, cancellationToken);
        }

        var result = await system.Query.Workers(query.Criteria, cancellationToken: cancellationToken);
        return new WorkViewWorkerGridDetailedComponent(
            result.Workers.Select(CreateWorkerGridDetailed).ToArray(),
            result.TotalCount,
            result.Skip,
            result.Take);
    }

    private static async Task<WorkViewWorkerGridDetailedComponent> CreateWorkerGridByKeyTypeComponent(
        IWorkSystem system,
        WorkSystemCriteria? scope,
        WorkViewWorkerGridCriteria query,
        CancellationToken cancellationToken)
    {
        var keyTypes = await system.Query.WorkerKeyTypes(new WorkerKeyTypeCriteria(
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
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var counts = await system.Query.SystemIterationCounts(criteria, cancellationToken: cancellationToken);
        if (shape == WorkComponentShapes.Compact)
        {
            return new WorkOverviewIterationsCompactComponent(
                counts.IterationCountByStatus);
        }

        var keyTypes = await system.Query.SystemCommonKeyTypes(criteria, cancellationToken: cancellationToken);
        return new WorkOverviewIterationsStandardComponent(
            counts.IterationCountByStatus,
            keyTypes.KeyTypes);
    }

    private static async Task<object> CreateFailedIterationsComponent(
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var iterations = await system.Query.SystemFailedIterations(criteria, cancellationToken: cancellationToken);
        return CreateIterationListComponent(iterations.Iterations, shape);
    }

    private static async Task<object> CreateCompletedIterationsComponent(
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        string shape,
        CancellationToken cancellationToken)
    {
        var iterations = await system.Query.SystemCompletedIterations(criteria, cancellationToken: cancellationToken);
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
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var query = CreateIterationGridCriteria(criteria, options);
        if (!string.IsNullOrWhiteSpace(query.KeyType))
        {
            return await CreateIterationGridByKeyTypeComponent(system, criteria, query, cancellationToken);
        }

        var result = await system.Query.WorkerIterations(query.Criteria, cancellationToken: cancellationToken);
        return new WorkViewIterationGridDetailedComponent(
            result.Iterations.Select(CreateIterationGridDetailed).ToArray(),
            result.TotalCount,
            result.Skip,
            result.Take);
    }

    private static async Task<WorkViewIterationGridDetailedComponent> CreateIterationGridByKeyTypeComponent(
        IWorkSystem system,
        WorkSystemCriteria? scope,
        WorkViewIterationGridCriteria query,
        CancellationToken cancellationToken)
    {
        var keyTypes = await system.Query.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(
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
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        string shape,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var workerCounts = await system.Query.SystemWorkerCounts(criteria, cancellationToken: cancellationToken);
        var throughputCriteria = CreateThroughputCriteria(options);
        if (shape == WorkComponentShapes.Compact)
        {
            var summary = await system.Query.SystemThroughputSummary(
                criteria,
                throughputCriteria,
                cancellationToken: cancellationToken);
            return new WorkOverviewThroughputCompactComponent(
                workerCounts.ActiveWorkerCount,
                new WorkOverviewThroughputCompact(
                    summary.WindowSeconds,
                    summary.ExecutionSummary,
                    CreateLiveSummary(summary.LiveSummary)));
        }

        var throughput = await system.Query.SystemThroughput(
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

        return request with { Shape = shape };
    }

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
}
