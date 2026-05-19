namespace Workable;

public sealed class WorkableHttpSystemResolver(
    IWorkSystemRegistry registry,
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

    internal static async Task<WorkableHttpSystemLifecycleResult> Start(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        await system.Start(cancellationToken);
        return new WorkableHttpSystemLifecycleResult(system.Id, system.Name, system.State);
    }

    internal static WorkableHttpSystemDiagnostics Diagnostics(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return new WorkableHttpSystemDiagnostics(
            system.Id,
            system.Name,
            system.State,
            system.Diagnostics.Queue,
            system.Diagnostics.ReadModel,
            system.Diagnostics.Retention,
            system.Diagnostics.Concurrency,
            system.Diagnostics.Durability,
            system.Diagnostics.Idempotency);
    }

    internal static async Task<WorkableHttpSystemStopResult> Stop(
        IWorkSystem system,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(origin);

        var result = await WorkableHttpOriginAwareSystem.Required(system).Stop(origin, cancellationToken);
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

    private WorkableHttpCapabilities CreateCapabilities(IWorkSystem system)
    {
        var realtime = realtimeCapabilityProviders.FirstOrDefault()?.GetCapability(system)
            ?? WorkRealtimeCapability.Disabled;
        return new WorkableHttpCapabilities(realtime);
    }
}
