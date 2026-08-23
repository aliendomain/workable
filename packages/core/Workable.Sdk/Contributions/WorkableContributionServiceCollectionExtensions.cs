using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;
/// <summary>
/// Registers feature-owned work contributions that a host can include in one or more Workable systems.
/// </summary>
public static class WorkableContributionServiceCollectionExtensions
{
    /// <summary>
    /// Registers untyped delegate-based work as a feature contribution.
    /// </summary>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure: null, authorize: null, systemName);

    /// <summary>
    /// Registers untyped delegate-based work as a feature contribution and applies fluent configuration.
    /// </summary>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure, authorize: null, systemName);

    /// <summary>
    /// Registers untyped delegate-based work as a feature contribution and applies fluent configuration and authorization.
    /// </summary>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize,
        string? systemName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        services.RegisterInitializerTypes(registration);
        services.AddSingleton(new WorkContribution(
            registration.Definition,
            systemName,
            _ => new DelegateWorkExecutor(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));

        return services;
    }

    /// <summary>
    /// Registers typed-input delegate-based work as a feature contribution.
    /// </summary>
    /// <typeparam name="TInput">The logical input type Workable should deserialize for the delegate.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TInput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure: null, authorize: null, systemName);

    /// <summary>
    /// Registers typed-input delegate-based work as a feature contribution and applies fluent configuration.
    /// </summary>
    /// <typeparam name="TInput">The logical input type Workable should deserialize for the delegate.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TInput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure, authorize: null, systemName);

    /// <summary>
    /// Registers typed-input delegate-based work as a feature contribution and applies fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TInput">The logical input type Workable should deserialize for the delegate.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TInput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize,
        string? systemName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        services.RegisterInitializerTypes(registration);
        services.AddSingleton(new WorkContribution(
            registration.Definition,
            systemName,
            _ => new TypedDelegateWorkExecutor<TInput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));

        return services;
    }

    /// <summary>
    /// Registers typed-input, typed-output delegate-based work as a feature contribution.
    /// </summary>
    /// <typeparam name="TInput">The logical input type Workable should deserialize for the delegate.</typeparam>
    /// <typeparam name="TOutput">The logical output type Workable should serialize from the execution result.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TInput, TOutput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure: null, authorize: null, systemName);

    /// <summary>
    /// Registers typed-input, typed-output delegate-based work as a feature contribution and applies fluent configuration.
    /// </summary>
    /// <typeparam name="TInput">The logical input type Workable should deserialize for the delegate.</typeparam>
    /// <typeparam name="TOutput">The logical output type Workable should serialize from the execution result.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TInput, TOutput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure, authorize: null, systemName);

    /// <summary>
    /// Registers typed-input, typed-output delegate-based work as a feature contribution and applies fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TInput">The logical input type Workable should deserialize for the delegate.</typeparam>
    /// <typeparam name="TOutput">The logical output type Workable should serialize from the execution result.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TInput, TOutput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize,
        string? systemName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput, TOutput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        services.RegisterInitializerTypes(registration);
        services.AddSingleton(new WorkContribution(
            registration.Definition,
            systemName,
            _ => new TypedDelegateWorkExecutor<TInput, TOutput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));

        return services;
    }

    /// <summary>
    /// Registers service-backed work as a feature contribution using metadata discovered from <typeparamref name="TExecutor"/>.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable should resolve from dependency injection.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure: null,
            authorize: null,
            systemName);

    /// <summary>
    /// Registers service-backed work as a feature contribution using executor metadata and fluent configuration.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable should resolve from dependency injection.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        Action<IWorkConfigurationBuilder> configure,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(configure, authorize: null, systemName);

    /// <summary>
    /// Registers service-backed work as a feature contribution using executor metadata plus fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable should resolve from dependency injection.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure,
            authorize,
            systemName);

    /// <summary>
    /// Registers service-backed work as a feature contribution using an explicit definition.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable should resolve from dependency injection.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        WorkDefinition definition,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(definition, configure: null, authorize: null, systemName);

    /// <summary>
    /// Registers service-backed work as a feature contribution using an explicit definition and fluent configuration.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable should resolve from dependency injection.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(definition, configure, authorize: null, systemName);

    /// <summary>
    /// Registers service-backed work as a feature contribution using an explicit definition plus fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable should resolve from dependency injection.</typeparam>
    /// <param name="services">The service collection that should receive the work contribution.</param>
    /// <param name="definition">The definition metadata and baseline configuration for the contributed work.</param>
    /// <param name="configure">The callback that refines the work configuration for this contribution.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this contribution.</param>
    /// <param name="systemName">
    /// The optional target system name. Leave this <see langword="null"/> to contribute the work to any system
    /// that includes unbound feature work.
    /// </param>
    /// <returns>The same service collection so additional services or contributions can be registered.</returns>
    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize,
        string? systemName = null)
        where TExecutor : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definition);
        WorkExecutorAdapterFactory.ThrowIfUnsupported(typeof(TExecutor));

        services.AddScoped<TExecutor>();
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, typeof(TExecutor), configure, authorize);
        services.RegisterInitializerTypes(registration);
        services.AddSingleton(new WorkContribution(
            registration.Definition,
            systemName,
            serviceProvider => WorkExecutorAdapterFactory.Create(serviceProvider.GetRequiredService<TExecutor>()),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));

        return services;
    }

    private static void RegisterInitializerTypes(
        this IServiceCollection services,
        WorkRegistrationConfiguration registration)
    {
        foreach (var initializerType in registration.InitializerTypes)
        {
            services.TryAddScoped(initializerType);
        }
    }
}
