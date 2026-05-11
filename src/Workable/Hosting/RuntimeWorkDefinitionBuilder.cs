using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed class RuntimeWorkDefinitionBuilder(WorkSystemCatalog catalog) : IWorkDefinitionBuilder
{
    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null);

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            _ => new DelegateWorkExecutor(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null);

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute)
        => this.AddWork(definition, execute, configure: null);

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput, TOutput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput, TOutput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>()
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure)
        where TExecutor : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkExecutorAdapterFactory.ThrowIfUnsupported(typeof(TExecutor));

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, typeof(TExecutor), configure);
        catalog.AddWork(new RegisteredWork(
            registration.Definition,
            serviceProvider => WorkExecutorAdapterFactory.Create(serviceProvider.GetRequiredService<TExecutor>()),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure);
}
