using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
/// <summary>
/// Registers hosted Workable systems and host-wide Workable services with dependency injection.
/// </summary>
public static class WorkableServiceCollectionExtensions
{
    private static readonly Type HostedServiceType = typeof(WorkableHostedService);

    /// <summary>
    /// Adds host-wide Workable configuration that applies across all systems registered in the container.
    /// </summary>
    /// <param name="services">The service collection that should receive the Workable host services.</param>
    /// <param name="configure">The callback that configures global Workable behavior.</param>
    /// <returns>The same service collection so additional application services can be registered.</returns>
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

    /// <summary>
    /// Adds the default Workable system to the service collection.
    /// </summary>
    /// <param name="services">The service collection that should receive the system registration.</param>
    /// <param name="configure">The callback that configures the system before the container is built.</param>
    /// <returns>The same service collection so additional application services can be registered.</returns>
    public static IServiceCollection AddWorkableSystem(
        this IServiceCollection services,
        Action<IWorkSystemBuilder> configure)
        => services.AddWorkableSystem(name: null, configure);

    /// <summary>
    /// Adds a named Workable system to the service collection.
    /// </summary>
    /// <param name="services">The service collection that should receive the system registration.</param>
    /// <param name="name">
    /// The optional system name. Use <see langword="null"/> to register the default unnamed system.
    /// </param>
    /// <param name="configure">The callback that configures the system before the container is built.</param>
    /// <returns>The same service collection so additional application services can be registered.</returns>
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
        services.TryAddSingleton<IWorkCommandDispatcher, WorkCommandDispatcher>();
        services.TryAddSingleton<IWorkflowCommandDispatcher, WorkflowCommandDispatcher>();
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
        services.RemoveAll<IWorkProfilingContextAccessor>();
        services.AddSingleton<IWorkProfilingContextAccessor, WorkProfilingContextAccessor>();
    }
}
