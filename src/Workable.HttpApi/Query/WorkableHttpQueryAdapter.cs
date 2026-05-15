using System.Text.Json;

namespace Workable;

public sealed class WorkableHttpQueryAdapter
{
    public async Task<WorkComponentQueryResult> Components(
        IWorkSystem system,
        WorkComponentCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        var query = criteria ?? new WorkComponentCriteria();
        var requests = NormalizeComponentRequests(query.Components);
        var details = new Lazy<Task<WorkSystemDetails>>(() => system.Query.SystemDetails(query.Scope, cancellationToken: cancellationToken));
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            components[request.Id] = await this.CreateComponent(system, details, request, query.Scope, cancellationToken);
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
        if (!string.Equals(name, "overview", StringComparison.OrdinalIgnoreCase))
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
                NormalizeComponentRequests(query.Components)),
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
        Lazy<Task<WorkSystemDetails>> details,
        WorkComponentRequest request,
        WorkSystemCriteria? criteria,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = request.Type.Trim().ToLowerInvariant() switch
            {
                "system" => CreateSystemComponent(await details.Value),
                "catalog" => CreateCatalogComponent(await details.Value),
                "workers" => CreateWorkerSummaryComponent(await details.Value),
                "failedworkers" => (await details.Value).FailedWorkers,
                "relationships" => CreateRelationshipsComponent(await details.Value),
                "failediterations" => (await details.Value).FailedIterations,
                "completediterations" => (await details.Value).CompletedIterations,
                "throughput" => await CreateThroughputComponent(system, criteria, request.Options, cancellationToken),
                _ => null,
            };

            return data is null
                ? new WorkComponentResult("error", Error: $"Unknown component '{request.Type}'.")
                : new WorkComponentResult("ok", data);
        }
        catch (Exception exception)
        {
            return new WorkComponentResult("error", Error: exception.Message);
        }
    }

    private static object CreateSystemComponent(WorkSystemDetails details)
        => new
        {
            details.SystemName,
            details.SystemState,
        };

    private static object CreateCatalogComponent(WorkSystemDetails details)
        => new
        {
            details.CatalogCategories,
            details.CatalogDefinitions,
        };

    private static object CreateWorkerSummaryComponent(WorkSystemDetails details)
        => new
        {
            details.DefinitionCount,
            details.ActiveWorkerCount,
            details.FinalWorkerCount,
            details.FailedWorkerCount,
            details.WorkerCountByState,
        };

    private static object CreateRelationshipsComponent(WorkSystemDetails details)
        => new
        {
            details.CurrentIterationCount,
            details.CompletedIterationCount,
            details.FailedIterationCount,
            details.CanceledIterationCount,
            details.IterationCountByStatus,
            details.CommonKeyTypes,
        };

    private static async Task<object> CreateThroughputComponent(
        IWorkSystem system,
        WorkSystemCriteria? criteria,
        JsonElement? options,
        CancellationToken cancellationToken)
    {
        var workerCounts = await system.Query.SystemWorkerCounts(criteria, cancellationToken: cancellationToken);
        var throughput = await system.Query.SystemThroughput(
            criteria,
            CreateThroughputCriteria(options),
            cancellationToken: cancellationToken);
        return new
        {
            workerCounts.ActiveWorkerCount,
            Throughput = throughput,
        };
    }

    private static IReadOnlyList<WorkComponentRequest> NormalizeComponentRequests(
        IReadOnlyList<WorkComponentRequest>? requests)
        => requests is { Count: > 0 }
            ? requests
            : [
                new("system", "system"),
                new("catalog", "catalog"),
                new("workers", "workers"),
                new("failedWorkers", "failedWorkers"),
                new("relationships", "relationships"),
                new("failedIterations", "failedIterations"),
                new("completedIterations", "completedIterations"),
            ];

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

    private static int? TryGetInt32(JsonElement options, string propertyName)
        => options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value)
                ? value
                : null;
}
