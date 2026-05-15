namespace Workable;

public sealed class WorkableHttpWorkService(
    IWorkSystemRegistry registry,
    IDotNetWorkOriginProvider dotNetOriginProvider,
    IEnumerable<IWorkRealtimeCapabilityProvider> realtimeCapabilityProviders)
{
    public bool TryGetSystem(string? systemName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWorkSystem? system)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            system = registry.Default;
            return true;
        }

        return registry.TryGet(systemName, out system);
    }

    public IReadOnlyList<WorkDefinition> GetDefinitions()
        => GetDefinitions(registry.Default);

    public WorkableHttpSystems GetSystems()
    {
        var defaultSystemId = registry.Default.Id;
        var systems = registry.Systems
            .OrderBy(system => system.Name is null ? 0 : 1)
            .ThenBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
            .Select(system => new WorkableHttpSystemInfo(
                system.Id,
                system.Name,
                system.State,
                system.Id == defaultSystemId,
                this.CreateCapabilities(system)))
            .ToList();

        return new WorkableHttpSystems(systems);
    }

    private WorkableHttpCapabilities CreateCapabilities(IWorkSystem system)
    {
        var realtime = realtimeCapabilityProviders.FirstOrDefault()?.GetCapability(system)
            ?? WorkRealtimeCapability.Disabled;
        return new WorkableHttpCapabilities(realtime);
    }

    public Task<WorkableHttpWorkResult> Queue(
        string name,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => Queue(
            registry.Default,
            name,
            request,
            dotNetOriginProvider.CreateOrigin($"Queue HTTP-adapter work '{name}' through .NET."),
            cancellationToken);

    internal Task<WorkableHttpWorkResult> Queue(
        string name,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
        => Queue(registry.Default, name, request, origin, cancellationToken);

    public Task<WorkableHttpWorkResult> Queue(
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => Queue(
            registry.Default,
            definitionId,
            request,
            dotNetOriginProvider.CreateOrigin($"Queue HTTP-adapter work definition '{definitionId.Value:D}' through .NET."),
            cancellationToken);

    internal Task<WorkableHttpWorkResult> Queue(
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
        => Queue(registry.Default, definitionId, request, origin, cancellationToken);

    public Task<WorkerSnapshot?> GetWorker(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.GetWorker(workerId, cancellationToken);

    public Task<WorkerQueryResult> QueryWorkers(
        WorkerQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.QueryWorkers(query ?? new WorkerQuery(), cancellationToken);

    public Task<WorkInfo?> GetWorkInfo(
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.GetWorkInfo(definitionId, cancellationToken);

    public Task<WorkInfo?> GetWorkInfo(
        string name,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.GetWorkInfo(name, cancellationToken);

    public Task<IReadOnlyList<WorkDefinition>> QueryWorkDefinitions(
        WorkDefinitionQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.QueryWorkDefinitions(query ?? new WorkDefinitionQuery(), cancellationToken);

    public Task<WorkerKeyQueryResult> QueryWorkerKeys(
        WorkerKeyQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.QueryWorkerKeys(query ?? new WorkerKeyQuery(), cancellationToken);

    public Task<WorkerKeyTypeQueryResult> QueryWorkerKeyTypes(
        WorkerKeyTypeQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.QueryWorkerKeyTypes(query, cancellationToken);

    public Task<WorkIterationKeyQueryResult> QueryWorkIterationKeys(
        WorkIterationKeyQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.QueryWorkIterationKeys(query ?? new WorkIterationKeyQuery(), cancellationToken);

    public Task<WorkIterationKeyTypeQueryResult> QueryWorkIterationKeyTypes(
        WorkIterationKeyTypeQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.QueryWorkIterationKeyTypes(query, cancellationToken);

    public Task<WorkerStatusSummary> GetWorkerStatusSummary(
        WorkerQuery? query = null,
        CancellationToken cancellationToken = default)
        => registry.Default.Query.GetWorkerStatusSummary(query, cancellationToken);

    public Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinition(
        WorkDefinitionId definitionId,
        WorkableHttpDefinitionReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return registry.Default.Catalog.Reconfigure(
            new WorkDefinitionVersion(definitionId, request.Revision),
            request.Changes,
            cancellationToken);
    }

    internal static async Task<WorkableHttpSystemLifecycleResult> Start(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        await system.Start(cancellationToken);
        return new WorkableHttpSystemLifecycleResult(system.Id, system.Name, system.State);
    }

    internal static async Task<WorkableHttpSystemStopResult> Stop(
        IWorkSystem system,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(origin);

        var result = await RequiredOriginAwareSystem(system).Stop(origin, cancellationToken);
        return new WorkableHttpSystemStopResult(
            system.Id,
            system.Name,
            system.State,
            result.ForceCanceledWorkers)
        {
            CancellationRequestedWorkers = result.CancellationRequestedWorkers,
            CancellationRequestedWorkerSummaries = result.CancellationRequestedWorkerSummaries,
            ForceCanceledWorkerNames = result.ForceCanceledWorkerNames,
            ForceCanceledWorkerSummaries = result.ForceCanceledWorkerSummaries,
            ShutdownGracePeriod = result.ShutdownGracePeriod,
        };
    }

    public Task<WorkActionOutcome> Execute(
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return registry.Default.Workers.Execute(new WorkerVersion(workerId, request.Revision), action, cancellationToken);
    }

    internal Task<WorkActionOutcome> Execute(
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RequiredOriginAwareSystem().Execute(new WorkerVersion(workerId, request.Revision), action, origin, cancellationToken);
    }

    internal static Task<WorkActionOutcome> Execute(
        IWorkSystem system,
        WorkerId workerId,
        WorkAction action,
        WorkableHttpWorkerActionRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return RequiredOriginAwareSystem(system).Execute(new WorkerVersion(workerId, request.Revision), action, origin, cancellationToken);
    }

    internal static Task<WorkerBulkActionOutcome> ExecuteAll(
        IWorkSystem system,
        WorkAction action,
        WorkableHttpWorkerBulkActionRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(origin);

        return RequiredOriginAwareSystem(system).ExecuteAll(
            action,
            request?.ToFilter(),
            origin,
            cancellationToken);
    }

    public Task<WorkActionOutcome> Reconfigure(
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return registry.Default.Workers.Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, cancellationToken);
    }

    internal Task<WorkActionOutcome> Reconfigure(
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RequiredOriginAwareSystem().Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, origin, cancellationToken);
    }

    internal static Task<WorkActionOutcome> Reconfigure(
        IWorkSystem system,
        WorkerId workerId,
        WorkableHttpWorkerReconfigurationRequest request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return RequiredOriginAwareSystem(system).Reconfigure(new WorkerVersion(workerId, request.Revision), request.Changes, origin, cancellationToken);
    }

    internal static Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinition(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpDefinitionReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return system.Catalog.Reconfigure(
            new WorkDefinitionVersion(definitionId, request.Revision),
            request.Changes,
            cancellationToken);
    }

    internal static IReadOnlyList<WorkDefinition> GetDefinitions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return [.. system.Catalog.Definitions
            .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static WorkableHttpDefinitionCatalogLevel GetDefinitionCatalogLevel(
        IWorkSystem system,
        string? category)
    {
        ArgumentNullException.ThrowIfNull(system);

        IReadOnlyList<string> pathSegments = string.IsNullOrWhiteSpace(category)
            ? []
            : SplitCategoryPath(category);
        var categories = new Dictionary<string, WorkOverviewCatalogCategoryItem>(StringComparer.OrdinalIgnoreCase);
        var directDefinitions = new List<WorkDefinition>();

        foreach (var definition in system.Catalog.Definitions)
        {
            var definitionSegments = SplitCategoryPath(definition.Category);
            if (!StartsWithCategoryPath(definitionSegments, pathSegments))
            {
                continue;
            }

            var remainingSegments = definitionSegments.Skip(pathSegments.Count).ToArray();
            if (remainingSegments.Length == 0)
            {
                directDefinitions.Add(definition);
                continue;
            }

            var childSegments = pathSegments.Append(remainingSegments[0]).ToArray();
            var childPath = string.Join(':', childSegments);
            if (categories.TryGetValue(childPath, out var existing))
            {
                categories[childPath] = existing with { Count = existing.Count + 1 };
            }
            else
            {
                categories[childPath] = new WorkOverviewCatalogCategoryItem(
                    remainingSegments[0],
                    childPath,
                    1);
            }
        }

        return new WorkableHttpDefinitionCatalogLevel(
            [.. categories.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)],
            [.. directDefinitions
                .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static IReadOnlyList<string> SplitCategoryPath(string? category)
        => (string.IsNullOrWhiteSpace(category)
                ? WorkDefinitionMetadataDefaults.Category
                : category)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool StartsWithCategoryPath(
        IReadOnlyList<string> categorySegments,
        IReadOnlyList<string> pathSegments)
        => pathSegments.Count == 0 ||
            pathSegments.Count <= categorySegments.Count &&
            pathSegments
                .Select((segment, index) => string.Equals(
                    categorySegments[index],
                    segment,
                    StringComparison.OrdinalIgnoreCase))
                .All(matches => matches);

    internal static async Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        string name,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => await Queue(
            system,
            name,
            request,
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: $"Queue HTTP-adapter work '{name}' through .NET."),
            cancellationToken);

    internal static async Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        string name,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!system.Catalog.TryGet(name, out var definition))
        {
            var notFound = WorkQueueOutcome.NotFound(name);
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                notFound,
                WorkerId: null,
                Completion: null,
                Output: null,
                notFound.Messages);
        }

        if (!definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi))
        {
            var outcome = WorkQueueOutcome.Invalid(
                definition.Id,
                [WorkMessage.Error("workable.invocation.channel_not_allowed", $"Work '{name}' cannot be invoked through the HTTP API.", "invocation.channel")]);
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                outcome,
                WorkerId: null,
                Completion: null,
                Output: null,
                outcome.Messages);
        }

        var handle = await RequiredOriginAwareSystem(system).Enqueue(name, CreateInput(request), request?.Options?.ToWorkerOptions(), origin, cancellationToken);
        return await CreateQueueResult(handle, request, cancellationToken);
    }

    internal static async Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request = null,
        CancellationToken cancellationToken = default)
        => await Queue(
            system,
            definitionId,
            request,
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: $"Queue HTTP-adapter work definition '{definitionId.Value:D}' through .NET."),
            cancellationToken);

    internal static async Task<WorkableHttpWorkResult> Queue(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpWorkRequest? request,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (!system.Catalog.TryGet(definitionId, out var definition))
        {
            var notFound = WorkQueueOutcome.NotFound(definitionId.ToString());
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                notFound,
                WorkerId: null,
                Completion: null,
                Output: null,
                notFound.Messages);
        }

        if (!definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi))
        {
            var outcome = WorkQueueOutcome.Invalid(
                definition.Id,
                [WorkMessage.Error("workable.invocation.channel_not_allowed", $"Work '{definition.Name}' cannot be invoked through the HTTP API.", "invocation.channel")]);
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                outcome,
                WorkerId: null,
                Completion: null,
                Output: null,
                outcome.Messages);
        }

        var handle = await RequiredOriginAwareSystem(system).Enqueue(definitionId, CreateInput(request), request?.Options?.ToWorkerOptions(), origin, cancellationToken);
        return await CreateQueueResult(handle, request, cancellationToken);
    }

    private IOriginAwareWorkSystem RequiredOriginAwareSystem()
        => RequiredOriginAwareSystem(registry.Default);

    private static IOriginAwareWorkSystem RequiredOriginAwareSystem(IWorkSystem system)
        => system as IOriginAwareWorkSystem
            ?? throw new InvalidOperationException("The configured Workable system does not support trusted work origins.");

    private static async Task<WorkableHttpWorkResult> CreateQueueResult(
        IWorkerHandle handle,
        WorkableHttpWorkRequest? request,
        CancellationToken cancellationToken)
    {
        if (!handle.QueueOutcome.IsAccepted)
        {
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Rejected,
                handle.QueueOutcome,
                handle.WorkerId,
                Completion: null,
                Output: null,
                handle.QueueOutcome.Messages);
        }

        var completionMode = request?.Completion ?? WorkableHttpCompletion.ReturnAfterAccepted;
        if (completionMode == WorkableHttpCompletion.ReturnAfterAccepted)
        {
            return new WorkableHttpWorkResult(
                WorkableHttpWorkStatus.Accepted,
                handle.QueueOutcome,
                handle.WorkerId,
                Completion: null,
                Output: null,
                handle.QueueOutcome.Messages);
        }

        var completion = await handle.WaitForCompletion(cancellationToken);

        return new WorkableHttpWorkResult(
            ToHttpStatus(completion.Status),
            handle.QueueOutcome,
            handle.WorkerId,
            completion,
            completion.Output,
            completion.Messages);
    }

    private static WorkInput CreateInput(WorkableHttpWorkRequest? request)
    {
        var input = request?.Input is { } json
            ? WorkInput.FromJson(
                json.GetRawText(),
                subjectId: request.SubjectId,
                concurrencyKey: request.ConcurrencyKey,
                identifiers: request.Identifiers)
            : WorkInput.Empty;

        if (request is null)
        {
            return input;
        }

        if (request.SubjectId is { } subjectId && input.SubjectId is null)
        {
            input = input.WithSubject(subjectId);
        }

        if (request.ConcurrencyKey is { } concurrencyKey && input.ConcurrencyKey is null)
        {
            input = input.WithConcurrencyKey(concurrencyKey);
        }

        if (request.Identifiers is { Count: > 0 } identifiers)
        {
            input = input.WithIdentifiers(identifiers);
        }

        return input;
    }

    private static WorkableHttpWorkStatus ToHttpStatus(WorkCompletionStatus status)
        => status switch
        {
            WorkCompletionStatus.Completed => WorkableHttpWorkStatus.Completed,
            WorkCompletionStatus.Canceled => WorkableHttpWorkStatus.Canceled,
            WorkCompletionStatus.Failed => WorkableHttpWorkStatus.Failed,
            _ => WorkableHttpWorkStatus.Failed,
        };
}
