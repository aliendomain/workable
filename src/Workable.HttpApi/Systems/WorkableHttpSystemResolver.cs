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

    public WorkableHttpSystems GetSystems(WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        var defaultSystemId = registry.Default.Id;
        var systems = registry.Systems
            .Where(system => system.CanConnect(requestContext))
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
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        await system.Start(requestContext, cancellationToken);
        return new WorkableHttpSystemLifecycleResult(system.Id, system.Name, system.State);
    }

    internal static WorkableHttpSystemDiagnostics Diagnostics(
        IWorkSystem system,
        IWorkSystemSession session)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(session);

        return new WorkableHttpSystemDiagnostics(
            system.Id,
            system.Name,
            system.State,
            session.Diagnostics.Queue,
            session.Diagnostics.ReadModel,
            session.Diagnostics.Retention,
            session.Diagnostics.Concurrency,
            session.Diagnostics.Durability,
            session.Diagnostics.Idempotency);
    }

    internal static async Task<WorkableHttpSystemStopResult> Stop(
        IWorkSystem system,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        var result = await system.Stop(requestContext, cancellationToken);
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
        var persistentCoordinationAvailable = system is IWorkSystemCoordinationCapabilities capabilities &&
            capabilities.PersistentCoordinationAvailable;

        return new WorkableHttpCapabilities(realtime, persistentCoordinationAvailable);
    }
}
