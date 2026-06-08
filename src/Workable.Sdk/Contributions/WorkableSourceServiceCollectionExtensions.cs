using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

/// <summary>
/// Registers feature-owned runtime definition sources and startup work sources.
/// </summary>
public static class WorkableSourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a runtime work definition source contribution using a source type resolved from dependency injection.
    /// </summary>
    /// <typeparam name="TSource">The work definition source type Workable should resolve during system startup.</typeparam>
    /// <param name="services">The service collection that should receive the source contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the source to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
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

    /// <summary>
    /// Registers a runtime work definition source contribution using a custom factory.
    /// </summary>
    /// <typeparam name="TSource">The work definition source type returned by the factory.</typeparam>
    /// <param name="services">The service collection that should receive the source contribution.</param>
    /// <param name="sourceFactory">The factory Workable should call during system startup to create the source.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the source to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
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

    /// <summary>
    /// Registers a startup work source contribution using a source type resolved from dependency injection.
    /// </summary>
    /// <typeparam name="TSource">The startup work source type Workable should resolve when the system starts.</typeparam>
    /// <param name="services">The service collection that should receive the source contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the source to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
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

    /// <summary>
    /// Registers a startup work source contribution using a custom factory.
    /// </summary>
    /// <typeparam name="TSource">The startup work source type returned by the factory.</typeparam>
    /// <param name="services">The service collection that should receive the source contribution.</param>
    /// <param name="sourceFactory">The factory Workable should call when the system starts to create the source.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the source to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
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
