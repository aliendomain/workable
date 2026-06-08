using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;

internal static class WorkableHttpDebugRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/debug/realtime", (
            HttpContext httpContext,
            IWebHostEnvironment environment,
            WorkableHttpTopologyResolver topology,
            IServiceProvider services,
            string? connectionId,
            string? systemName) =>
        {
            if (!IsAllowed(httpContext, environment))
            {
                return Results.NotFound();
            }

            if (!topology.TryResolveSystem(systemName, out var system))
            {
                return Results.NotFound();
            }

            var eventSubscriptions = services.GetService<WorkableRealtimeEventSubscriptions>();
            var viewSubscriptions = services.GetService<WorkableRealtimeViewSubscriptions>();
            var workerOverviewSubscriptions = services.GetService<WorkableRealtimeWorkerOverviewSubscriptions>();
            var normalizedConnectionId = string.IsNullOrWhiteSpace(connectionId)
                ? null
                : connectionId.Trim();

            return Results.Ok(new WorkableRealtimeDebugSystemSnapshot(
                system.Name,
                system.State.ToString(),
                FilterByConnectionId(
                    eventSubscriptions?.GetDebugSubscriptions(system) ?? [],
                    normalizedConnectionId,
                    subscription => subscription.ConnectionId),
                FilterByConnectionId(
                    viewSubscriptions?.GetDebugSubscriptions(system) ?? [],
                    normalizedConnectionId,
                    subscription => subscription.ConnectionId),
                FilterByConnectionId(
                    workerOverviewSubscriptions?.GetDebugSubscriptions(system) ?? [],
                    normalizedConnectionId,
                    subscription => subscription.ConnectionId)));
        });
    }

    private static IReadOnlyList<T> FilterByConnectionId<T>(
        IReadOnlyList<T> subscriptions,
        string? connectionId,
        Func<T, string> getConnectionId)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(getConnectionId);

        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return subscriptions;
        }

        return [.. subscriptions.Where(subscription =>
            string.Equals(getConnectionId(subscription), connectionId, StringComparison.Ordinal))];
    }

    private static bool IsAllowed(HttpContext httpContext, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return true;
        }

        var remoteAddress = httpContext.Connection.RemoteIpAddress;
        return remoteAddress is null || IPAddress.IsLoopback(remoteAddress);
    }
}
