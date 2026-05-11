using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

public static class WorkableAspNetCoreServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableAspNetCoreOrigins(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        UseHttpContextDotNetOriginProvider(services);
        return services;
    }

    private static void UseHttpContextDotNetOriginProvider(IServiceCollection services)
    {
        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDotNetWorkOriginProvider));
        if (existing is not null && existing.ImplementationType != typeof(DefaultDotNetWorkOriginProvider))
        {
            return;
        }

        services.RemoveAll<IDotNetWorkOriginProvider>();
        services.AddSingleton<IDotNetWorkOriginProvider, HttpContextDotNetWorkOriginProvider>();
    }
}
