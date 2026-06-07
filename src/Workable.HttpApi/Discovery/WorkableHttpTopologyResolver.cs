namespace Workable;

public sealed class WorkableHttpTopologyResolver(
    IWorkSystemRegistry registry,
    IEnumerable<IWorkRealtimeCapabilityProvider> realtimeCapabilityProviders)
{
    public bool TryResolveSystem(string? systemName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWorkSystem? system)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            system = registry.Default;
            return true;
        }

        return registry.TryGet(systemName, out system);
    }

    public WorkableHttpHostDescriptor DescribeHost(WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        var defaultSystem = registry.Default;
        var systems = registry.Systems
            .Select(system => (System: system, Access: system.DescribeAccess(requestContext)))
            .Where(result => result.Access.HasAnyAccess())
            .OrderBy(result => result.System.Name is null ? 0 : 1)
            .ThenBy(result => result.System.Name, StringComparer.OrdinalIgnoreCase)
            .Select(result => new WorkableHttpSystemDescriptor(
                result.System.Name,
                result.System.State,
                ReferenceEquals(result.System, defaultSystem),
                CreateSystemCapabilities(result.System),
                result.Access))
            .ToList();

        return new WorkableHttpHostDescriptor(
            this.CreateHostCapabilities(),
            systems);
    }

    internal static async Task<WorkableHttpSystemLifecycleResult> Start(
        IWorkSystem system,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        await system.Start(requestContext, cancellationToken);
        return new WorkableHttpSystemLifecycleResult(system.Name, system.State);
    }

    internal static WorkableHttpSystemDiagnostics Diagnostics(
        IWorkSystem system,
        IWorkSystemSession session)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(session);

        return new WorkableHttpSystemDiagnostics(
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
            system.Name,
            system.State,
            result.ForceInterruptedWorkers)
        {
            CancellationRequestedWorkers = result.CancellationRequestedWorkers,
            CancellationRequestedWorkerSummaries = result.CancellationRequestedWorkerSummaries,
            ForceInterruptedWorkerNames = result.ForceInterruptedWorkerNames,
            ForceInterruptedWorkerSummaries = result.ForceInterruptedWorkerSummaries,
            ShutdownGracePeriod = result.ShutdownGracePeriod,
        };
    }

    private WorkableHttpHostCapabilities CreateHostCapabilities()
    {
        var realtime = realtimeCapabilityProviders.FirstOrDefault()?.GetCapability()
            ?? WorkRealtimeCapability.Disabled;

        return new WorkableHttpHostCapabilities(realtime);
    }

    private static WorkableHttpSystemCapabilities CreateSystemCapabilities(
        IWorkSystem system)
    {
        var persistentCoordinationAvailable = system is IWorkSystemCoordinationCapabilities capabilities &&
            capabilities.PersistentCoordinationAvailable;

        return new WorkableHttpSystemCapabilities(persistentCoordinationAvailable);
    }
}
