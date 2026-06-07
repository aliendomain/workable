using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

public static class WorkableAspNetCoreServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableAspNetCoreAuthorization(
        this IServiceCollection services,
        Action<WorkableAspNetCoreAuthorizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddAuthorization();
        services.AddOptions<WorkableAspNetCoreAuthorizationOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IWorkActorFactory, HttpContextWorkActorFactory>();
        services.TryAddSingleton<IWorkRequestContextFactory, HttpContextWorkRequestContextFactory>();
        services.TryAddSingleton<IHttpContextWorkCommandDispatcher, HttpContextWorkCommandDispatcher>();
        UseHttpContextAuthorizationGroupProvider(services);
        return services;
    }

    private static void UseHttpContextAuthorizationGroupProvider(IServiceCollection services)
    {
        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IWorkAuthorizationGroupProvider));
        if (existing is not null && existing.ImplementationType != typeof(HttpContextClaimsWorkAuthorizationGroupProvider))
        {
            return;
        }

        services.RemoveAll<IWorkAuthorizationGroupProvider>();
        services.AddSingleton<IWorkAuthorizationGroupProvider, HttpContextClaimsWorkAuthorizationGroupProvider>();
    }
}
