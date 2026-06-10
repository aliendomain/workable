using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed class RuntimeWorkDefinitionBuilder(WorkSystemCatalog catalog) : IWorkDefinitionBuilder
{
    public IWorkDefinitionBuilder WithWorkDefaults(
        Action<IWorkDefinitionBuilder> register,
        Action<IWorkConfigurationBuilder>? configure = null,
        Action<IWorkAuthorizationBuilder>? authorize = null)
    {
        ArgumentNullException.ThrowIfNull(register);

        register(new DefaultingWorkDefinitionBuilder(this, configure, authorize));
        return this;
    }

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            _ => new DelegateWorkExecutor(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput, TOutput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput, TOutput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>()
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure: null,
            authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkExecutorAdapterFactory.ThrowIfUnsupported(typeof(TExecutor));

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, typeof(TExecutor), configure, authorize);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            serviceProvider => WorkExecutorAdapterFactory.Create(serviceProvider.GetRequiredService<TExecutor>()),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure,
            authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure,
            authorize);
}
