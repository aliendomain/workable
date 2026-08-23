namespace Workable;

/// <summary>
/// Registers work definitions for a system, typically from a runtime definition source or grouped registration scope.
/// </summary>
public interface IWorkDefinitionBuilder
{
    /// <summary>
    /// Applies shared work-level defaults to a scoped group of registrations added inside <paramref name="register"/>.
    /// </summary>
    /// <param name="register">
    /// The callback that registers work definitions while the supplied configuration and authorization defaults are active.
    /// </param>
    /// <param name="configure">
    /// Optional work configuration that runs before any per-registration <c>configure</c> callback inside the group.
    /// Registration-specific child-execution grants cannot be declared through this callback.
    /// </param>
    /// <param name="authorize">
    /// Optional work authorization that runs before any per-registration <c>authorize</c> callback inside the group.
    /// </param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder WithWorkDefaults(
        Action<IWorkDefinitionBuilder> register,
        Action<IWorkConfigurationBuilder>? configure = null,
        Action<IWorkAuthorizationBuilder>? authorize = null);

    /// <summary>
    /// Registers delegate-based work that consumes raw <see cref="WorkInput"/> and returns an untyped execution result.
    /// </summary>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute);

    /// <summary>
    /// Registers delegate-based work and applies fluent configuration to the definition.
    /// </summary>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    /// <summary>
    /// Registers delegate-based work and applies fluent configuration and work-level authorization.
    /// </summary>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork(
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
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute);

    /// <summary>
    /// Registers delegate-based work with typed input and fluent configuration.
    /// </summary>
    /// <typeparam name="TInput">The logical input type that Workable deserializes for the delegate.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="execute">The delegate Workable invokes when a worker executes this definition.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TInput>(
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
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TInput>(
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
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TInput, TOutput>(
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
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TInput, TOutput>(
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
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    /// <summary>
    /// Registers service-backed work using an executor resolved from dependency injection.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work using metadata discovered from <typeparamref name="TExecutor"/>.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TExecutor>()
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work and applies fluent configuration to the definition.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work and applies fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="definition">The definition metadata and baseline configuration for the work.</param>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work from executor metadata and applies fluent configuration.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    /// <summary>
    /// Registers service-backed work from executor metadata and applies fluent configuration and authorization.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type Workable resolves for each worker execution.</typeparam>
    /// <param name="configure">The callback that refines the work configuration for this registration.</param>
    /// <param name="authorize">The callback that defines work-level discover, read, and operate authorization for this registration.</param>
    /// <returns>The same builder so additional definition registrations can be chained.</returns>
    IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class;
}
