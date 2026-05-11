using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkableHostedService(IWorkSystemRegistry registry, IEnumerable<WorkSystemRegistration> registrations) : IHostedService
{
    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        var autoStartIds = registrations
            .Where(registration => registration.StartWithHost)
            .Select(registration => registration.Id)
            .ToHashSet();

        foreach (var system in registry.Systems.Where(system => autoStartIds.Contains(system.Id)))
        {
            await system.Start(cancellationToken);
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        foreach (var system in registry.Systems)
        {
            await system.Stop(cancellationToken);
        }
    }
}
