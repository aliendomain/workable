using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;

namespace Workable;

/// <summary>
/// Registers the Workable SignalR realtime adapter and its supporting services.
/// </summary>
public static class WorkableSignalRServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Workable SignalR hub, realtime broadcaster, authorization filters, and view adapters.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional callback used to configure realtime transport behavior.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// This registration enables authenticated SignalR clients to subscribe to Workable event streams, worker lists,
    /// named views, and worker-overview updates. The default Workable payload serializer derives its settings from the
    /// host's JSON protocol options without modifying them. Registration adds SignalR's normal framework defaults but
    /// does not reorder protocol services or configure global or per-hub protocol acceptance. Endpoint mapping validates
    /// that the host's effective protocol choice is compatible with the selected Workable payload serializer. Mapping is
    /// performed separately by
    /// <see cref="WorkableSignalREndpointRouteBuilderExtensions.MapWorkableSignalR(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string?, bool, string?, bool)"/>.
    /// </remarks>
    public static IServiceCollection AddWorkableSignalR(
        this IServiceCollection services,
        Action<WorkableSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WorkableSignalROptions>();
        services.AddWorkableAspNetCoreAuthorization();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(WorkableSignalRServiceRegistration)))
        {
            return services;
        }

        services.AddSingleton<WorkableSignalRServiceRegistration>();

        var signalR = services.AddSignalR();
        signalR.AddHubOptions<WorkableRealtimeHub>(options =>
        {
            options.AddFilter<WorkableSignalRAuthenticationFilter>();
            options.AddFilter<WorkableSignalRAuthorizationFilter>();
        });

        services.TryAddSingleton<IWorkRealtimeCapabilityProvider, WorkableRealtimeCapabilityProvider>();
        services.TryAddSingleton<WorkableSignalRRegistration>();
        services.TryAddSingleton<WorkableSignalRAuthenticationFilter>();
        services.TryAddSingleton<WorkableSignalRAuthorizationFilter>();
        services.TryAddSingleton<IWorkableSignalRPayloadSerializer, WorkableSignalRJsonPayloadSerializer>();
        services.TryAddSingleton<WorkableViewQueryAdapter>();
        services.TryAddSingleton<WorkableRealtimeEventSubscriptions>();
        services.TryAddSingleton<WorkableRealtimeViewSubscriptions>();
        services.TryAddSingleton<WorkableRealtimeWorkerOverviewSubscriptions>();
        services.TryAddSingleton<IWorkableRealtimeTimerFactory, WorkableRealtimeTimerFactory>();
        services.TryAddSingleton<WorkableRealtimeBroadcastLaneRunner>();
        services.TryAddSingleton<WorkableRealtimeBroadcaster>();
        services.AddSingleton<IHostedService>(services => services.GetRequiredService<WorkableRealtimeBroadcaster>());
        services.AddSingleton<IWorkSystemLifecycleObserver>(services => services.GetRequiredService<WorkableRealtimeBroadcaster>());
        return services;
    }
}

internal sealed class WorkableSignalRServiceRegistration;
