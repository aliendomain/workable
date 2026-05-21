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
            .Select(system => (System: system, Access: system.DescribeAccess(requestContext)))
            .Where(result => result.Access.CanConnect)
            .OrderBy(result => result.System.Name is null ? 0 : 1)
            .ThenBy(result => result.System.Name, StringComparer.OrdinalIgnoreCase)
            .Select(result => new WorkableHttpSystemInfo(
                result.System.Id,
                result.System.Name,
                result.System.State,
                result.System.Id == defaultSystemId,
                this.CreateCapabilities(result.System, result.Access),
                result.Access))
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

    private WorkableHttpCapabilities CreateCapabilities(
        IWorkSystem system,
        WorkSystemAccessSummary access)
    {
        var realtime = realtimeCapabilityProviders.FirstOrDefault()?.GetCapability(system)
            ?? WorkRealtimeCapability.Disabled;
        var persistentCoordinationAvailable = system is IWorkSystemCoordinationCapabilities capabilities &&
            capabilities.PersistentCoordinationAvailable;

        return new WorkableHttpCapabilities(
            FilterRealtimeCapability(realtime, access),
            persistentCoordinationAvailable);
    }

    private static WorkRealtimeCapability FilterRealtimeCapability(
        WorkRealtimeCapability realtime,
        WorkSystemAccessSummary access)
    {
        if (!realtime.Enabled || realtime.Features is not { Count: > 0 })
        {
            return realtime;
        }

        var features = realtime.Features
            .Where(feature => IsRealtimeFeatureAllowed(feature, access))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return realtime with
        {
            Enabled = features.Length > 0,
            Features = features,
        };
    }

    private static bool IsRealtimeFeatureAllowed(
        string feature,
        WorkSystemAccessSummary access)
        => feature switch
        {
            "system-view" => access.CanConnect,
            "work-views" => access.CanReadAllWork || access.ReadableDefinitionCount > 0,
            "worker-events" => access.CanReadAllWork || access.ReadableDefinitionCount > 0,
            "diagnostics-view" => access.CanViewDiagnostics,
            _ => true,
        };
}
