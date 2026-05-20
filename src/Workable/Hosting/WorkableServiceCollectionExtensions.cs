using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
public static class WorkableServiceCollectionExtensions
{
    private static readonly Type HostedServiceType = typeof(WorkableHostedService);

    public static IServiceCollection AddWorkable(
        this IServiceCollection services,
        Action<IWorkableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkableBuilder();
        configure(builder);
        services.AddSingleton(builder.Build());
        services.UseWorkableLogging();

        return services;
    }

    public static IServiceCollection AddWorkableSystem(
        this IServiceCollection services,
        Action<IWorkSystemBuilder> configure)
        => services.AddWorkableSystem(name: null, configure);

    public static IServiceCollection AddWorkableSystem(
        this IServiceCollection services,
        string? name,
        Action<IWorkSystemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkSystemBuilder(services, name);
        configure(builder);

        services.UseWorkableLogging();
        services.AddSingleton(builder.BuildRegistration());
        services.AddSingleton<IWorkSystemRegistry, WorkSystemRegistry>();
        services.TryAddSingleton(services => services.GetRequiredService<IWorkSystemRegistry>().Default);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == HostedServiceType))
        {
            services.AddSingleton<IHostedService, WorkableHostedService>();
        }

        return services;
    }

    private static void UseWorkableLogging(this IServiceCollection services)
    {
        services.RemoveAll(typeof(ILogger<>));
        services.AddSingleton(typeof(ILogger<>), typeof(WorkableLogger<>));
        services.RemoveAll<IWorkProfiler>();
        services.AddSingleton<IWorkProfiler, WorkProfilerFacade>();
    }
}
