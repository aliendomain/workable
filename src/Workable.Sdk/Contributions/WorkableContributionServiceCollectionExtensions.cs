using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;
public static class WorkableContributionServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableWork(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure: null, authorize: null, systemName);

    public static IServiceCollection AddWorkableWork(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure, authorize: null, systemName);

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

    public static IServiceCollection AddWorkableWork<TInput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure: null, authorize: null, systemName);

    public static IServiceCollection AddWorkableWork<TInput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure, authorize: null, systemName);

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

    public static IServiceCollection AddWorkableWork<TInput, TOutput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure: null, authorize: null, systemName);

    public static IServiceCollection AddWorkableWork<TInput, TOutput>(
        this IServiceCollection services,
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        => services.AddWorkableWork(definition, execute, configure, authorize: null, systemName);

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

    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure: null,
            authorize: null,
            systemName);

    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        Action<IWorkConfigurationBuilder> configure,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(configure, authorize: null, systemName);

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

    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        WorkDefinition definition,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(definition, configure: null, authorize: null, systemName);

    public static IServiceCollection AddWorkableWork<TExecutor>(
        this IServiceCollection services,
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        string? systemName = null)
        where TExecutor : class
        => services.AddWorkableWork<TExecutor>(definition, configure, authorize: null, systemName);

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
