using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;
public static class WorkableSignalRServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableSignalR(
        this IServiceCollection services,
        Action<WorkableSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WorkableSignalROptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services
            .AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        services.TryAddSingleton<IWorkRealtimeCapabilityProvider, WorkableRealtimeCapabilityProvider>();
        services.TryAddSingleton<WorkableViewQueryAdapter>();
        services.TryAddSingleton<WorkableRealtimeEventSubscriptions>();
        services.TryAddSingleton<WorkableRealtimeViewSubscriptions>();
        services.AddHostedService<WorkableRealtimeBroadcaster>();
        return services;
    }
}
