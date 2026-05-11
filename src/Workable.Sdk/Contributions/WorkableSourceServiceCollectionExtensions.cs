using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

public static class WorkableSourceServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableWorkDefinitionSource<TSource>(
        this IServiceCollection services,
        string? systemName = null)
        where TSource : class, IWorkDefinitionSource
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<TSource>();
        return services.AddWorkableWorkDefinitionSource(
            serviceProvider => serviceProvider.GetRequiredService<TSource>(),
            systemName);
    }

    public static IServiceCollection AddWorkableWorkDefinitionSource<TSource>(
        this IServiceCollection services,
        Func<IServiceProvider, TSource> sourceFactory,
        string? systemName = null)
        where TSource : class, IWorkDefinitionSource
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceFactory);

        services.AddSingleton(new WorkDefinitionSourceContribution(
            systemName,
            serviceProvider => sourceFactory(serviceProvider)));
        return services;
    }

    public static IServiceCollection AddWorkableStartupWorkSource<TSource>(
        this IServiceCollection services,
        string? systemName = null)
        where TSource : class, IStartupWorkSource
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<TSource>();
        return services.AddWorkableStartupWorkSource(
            serviceProvider => serviceProvider.GetRequiredService<TSource>(),
            systemName);
    }

    public static IServiceCollection AddWorkableStartupWorkSource<TSource>(
        this IServiceCollection services,
        Func<IServiceProvider, TSource> sourceFactory,
        string? systemName = null)
        where TSource : class, IStartupWorkSource
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceFactory);

        services.AddSingleton(new StartupWorkSourceContribution(
            systemName,
            serviceProvider => sourceFactory(serviceProvider)));
        return services;
    }
}
