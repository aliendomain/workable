namespace Workable;

/// <summary>
/// Resolves systems and projects host/system discovery data for the HTTP API.
/// </summary>
public sealed class WorkableHttpTopologyResolver(
    IWorkSystemRegistry registry,
    IEnumerable<IWorkRealtimeCapabilityProvider> realtimeCapabilityProviders)
{
    /// <summary>
    /// Resolves the default or a named system for an HTTP request.
    /// </summary>
    /// <param name="systemName">The requested system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="system">When this method returns <see langword="true"/>, receives the resolved system.</param>
    /// <returns><see langword="true"/> when the requested system exists; otherwise <see langword="false"/>.</returns>
    public bool TryResolveSystem(string? systemName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWorkSystem? system)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            system = registry.Default;
            return true;
        }

        return registry.TryGet(systemName, out system);
    }

    /// <summary>
    /// Builds the host-level discovery payload visible to the caller.
    /// </summary>
    /// <param name="requestContext">The caller context used to determine system visibility and access summaries.</param>
    /// <returns>The host discovery payload for the caller.</returns>
    public Task<WorkableHttpHostDescriptor> DescribeHost(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
        => this.DescribeHost(
            system => system.DescribeAccess(requestContext, cancellationToken),
            static (_, access) => ValueTask.FromResult(access.HasAnyAccess()));

    internal Task<WorkableHttpHostDescriptor> DescribeBuiltInSurfaceHost(
        WorkableHttpRequestAccessContext requestAccess,
        CancellationToken cancellationToken = default)
        => this.DescribeHost(
            system => requestAccess.DescribeAccess(system, cancellationToken),
            async (system, access) =>
                await requestAccess.IsBuiltInSurfaceAllowed(system, cancellationToken) && access.HasAnyAccess());

    private async Task<WorkableHttpHostDescriptor> DescribeHost(
        Func<IWorkSystem, ValueTask<WorkSystemAccessSummary>> describeAccess,
        Func<IWorkSystem, WorkSystemAccessSummary, ValueTask<bool>> includeSystem)
    {
        ArgumentNullException.ThrowIfNull(describeAccess);
        ArgumentNullException.ThrowIfNull(includeSystem);

        var defaultSystem = registry.Default;
        var visibleSystems = new List<(IWorkSystem System, WorkSystemAccessSummary Access)>();
        foreach (var system in registry.Systems)
        {
            var access = await describeAccess(system);
            if (await includeSystem(system, access))
            {
                visibleSystems.Add((system, access));
            }
        }

        var systems = visibleSystems
            .OrderBy(result => result.System.Name is null ? 0 : 1)
            .ThenBy(result => result.System.Name, StringComparer.OrdinalIgnoreCase)
            .Select(result => new WorkableHttpSystemDescriptor(
                result.System.Name,
                result.System.State,
                ReferenceEquals(result.System, defaultSystem),
                CreateSystemCapabilities(result.System),
                CreateTransportAccessSummary(result.Access)))
            .ToList();

        return new WorkableHttpHostDescriptor(
            this.CreateHostCapabilities(),
            systems);
    }

    private static WorkSystemAccessSummary CreateTransportAccessSummary(WorkSystemAccessSummary access)
        => access.CanDiscoverAllWork
            ? access
            : access with
            {
                TotalDefinitionCount = access.DiscoverableDefinitionCount,
            };

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
        var realtime = realtimeCapabilityProviders.LastOrDefault()?.GetCapability()
            ?? WorkRealtimeCapability.Disabled;

        return new WorkableHttpHostCapabilities(realtime);
    }

    private static WorkSystemCapabilities CreateSystemCapabilities(
        IWorkSystem system)
        => system is IWorkSystemCapabilitySource capabilities
            ? capabilities.Capabilities
            : WorkSystemCapabilities.None;
}
