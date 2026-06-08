using System.Diagnostics.CodeAnalysis;

namespace Workable;
/// <summary>
/// Configures one hosted Workable system during service registration.
/// </summary>
public interface IWorkSystemBuilder
{
    /// <summary>
    /// Applies shared work-level defaults to a scoped group of registrations added inside <paramref name="register"/>.
    /// </summary>
    /// <param name="register">
    /// The callback that registers work definitions while the supplied configuration and authorization defaults are active.
    /// </param>
    /// <param name="configure">
    /// Optional work configuration that runs before any per-registration <c>configure</c> callback inside the group.
    /// </param>
    /// <param name="authorize">
    /// Optional work authorization that runs before any per-registration <c>authorize</c> callback inside the group.
    /// </param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder WithWorkDefaults(
        Action<IWorkDefinitionBuilder> register,
        Action<IWorkConfigurationBuilder>? configure = null,
        Action<IWorkAuthorizationBuilder>? authorize = null);

    /// <summary>
    /// Registers delegate-based work that consumes raw <see cref="WorkInput"/> and returns an untyped execution result.
    /// </summary>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute);

    /// <summary>
    /// Registers delegate-based work and applies fluent configuration to the definition.
    /// </summary>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    /// <summary>
    /// Registers delegate-based work and applies fluent configuration and work-level authorization.
    /// </summary>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level read and operate authorization for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    /// <summary>
    /// Registers delegate-based work with typed input.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute);

    /// <summary>
    /// Registers delegate-based work with typed input and fluent configuration.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    /// <summary>
    /// Registers delegate-based work with typed input plus fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level read and operate authorization for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    /// <summary>
    /// Registers delegate-based work with typed input and typed output.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <typeparam name="TOutput">The logical output type that Workable serializes from the execution result.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute);

    /// <summary>
    /// Registers delegate-based work with typed input, typed output, and fluent configuration.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <typeparam name="TOutput">The logical output type that Workable serializes from the execution result.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder> configure);

    /// <summary>
    /// Registers delegate-based work with typed input, typed output, configuration, and authorization.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <typeparam name="TOutput">The logical output type that Workable serializes from the execution result.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level read and operate authorization for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    /// <summary>
    /// Registers service-backed work using an executor resolved from dependency injection.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work using metadata discovered from <typeparamref name="TExecutor"/>.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TExecutor>()
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work and applies fluent configuration to the definition.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work and applies fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level read and operate authorization for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work from executor metadata and applies fluent configuration.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work from executor metadata and applies fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level read and operate authorization for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class;

    /// <summary>
    /// Adds a runtime definition source that contributes work to this system during startup.
    /// </summary>
    /// <typeparam name="TSource">The source type Workable resolves from dependency injection.</typeparam>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWorkDefinitionSource<TSource>()
        where TSource : class, IWorkDefinitionSource;

    /// <summary>
    /// Adds a runtime definition source using a custom factory.
    /// </summary>
    /// <typeparam name="TSource">The source type returned by the factory.</typeparam>
    /// <param name="sourceFactory">The factory Workable calls to create the source during system startup.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddWorkDefinitionSource<TSource>(Func<IServiceProvider, TSource> sourceFactory)
        where TSource : class, IWorkDefinitionSource;

    /// <summary>
    /// Adds a startup work source that produces queue requests when the system starts.
    /// </summary>
    /// <typeparam name="TSource">The source type Workable resolves from dependency injection.</typeparam>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddStartupWorkSource<TSource>()
        where TSource : class, IStartupWorkSource;

    /// <summary>
    /// Adds a startup work source using a custom factory.
    /// </summary>
    /// <typeparam name="TSource">The source type returned by the factory.</typeparam>
    /// <param name="sourceFactory">The factory Workable calls to create the source during system startup.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder AddStartupWorkSource<TSource>(Func<IServiceProvider, TSource> sourceFactory)
        where TSource : class, IStartupWorkSource;

    /// <summary>
    /// Controls whether unbound feature contributions are included in this system.
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> to include contributed work, definition sources, and startup sources;
    /// <see langword="false"/> to opt this system out of them.
    /// </param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder IncludeContributedWork(bool enabled = true);

    /// <summary>
    /// Controls whether Workable enforces authorization on this system.
    /// </summary>
    /// <param name="required">
    /// <see langword="true"/> to require system and work authorization, or <see langword="false"/> to leave the system open.
    /// </param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder RequireAuthorization(bool required = true);

    /// <summary>
    /// Configures system-level authorization roles and grants.
    /// </summary>
    /// <param name="configure">The callback that defines system-wide access rules for this registration.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder ConfigureAuthorization(Action<IWorkSystemAuthorizationBuilder> configure);

    /// <summary>
    /// Controls whether the system starts automatically with the host.
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to start automatically with the host; otherwise <see langword="false"/>.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder StartWithHost(bool enabled = true);

    /// <summary>
    /// Sets an explicit shutdown grace period for this system.
    /// </summary>
    /// <param name="gracePeriod">The amount of time Workable should allow workers to finish during shutdown.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder UseShutdownGracePeriod(TimeSpan gracePeriod);

    /// <summary>
    /// Sets the shutdown grace period as a ratio of the host shutdown timeout.
    /// </summary>
    /// <param name="hostShutdownTimeoutRatio">The fraction of the host timeout that Workable may consume during shutdown.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder UseShutdownGracePeriodRatio(double hostShutdownTimeoutRatio);

    /// <summary>
    /// Replaces the system-wide retention settings.
    /// </summary>
    /// <param name="retention">The retention configuration to apply to the system.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder UseRetention(WorkSystemRetentionConfiguration retention);

    /// <summary>
    /// Configures the maximum number of final-state workers the system retains.
    /// </summary>
    /// <param name="maximumFinalWorkers">The maximum number of final-state workers to retain across the system.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder ConfigureRetention(int? maximumFinalWorkers = null);

    /// <summary>
    /// Replaces the system-wide capacity settings.
    /// </summary>
    /// <param name="capacity">The capacity configuration to apply to the system.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder UseCapacity(WorkSystemCapacityConfiguration capacity);

    /// <summary>
    /// Configures the maximum number of workers the system may retain in memory and scheduling state.
    /// </summary>
    /// <param name="maximumWorkers">The maximum worker capacity for the system.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder ConfigureCapacity(int? maximumWorkers = null);

    /// <summary>
    /// Adds an exception classifier that applies to work registered in this system.
    /// </summary>
    /// <param name="classifier">The classifier Workable evaluates when work in this system throws.</param>
    /// <returns>The same builder so additional system configuration can be chained.</returns>
    IWorkSystemBuilder ClassifyExceptions(WorkExceptionClassifier classifier);

}
