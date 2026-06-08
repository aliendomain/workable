namespace Workable;

internal sealed class SystemWorkDefinitionBuilderAdapter(IWorkSystemBuilder inner) : IWorkDefinitionBuilder
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
    {
        inner.AddWork(definition, execute);
        return this;
    }

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure)
    {
        inner.AddWork(definition, execute, configure);
        return this;
    }

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        inner.AddWork(definition, execute, configure, authorize);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute)
    {
        inner.AddWork(definition, execute);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure)
    {
        inner.AddWork(definition, execute, configure);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        inner.AddWork(definition, execute, configure, authorize);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute)
    {
        inner.AddWork(definition, execute);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder> configure)
    {
        inner.AddWork(definition, execute, configure);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        inner.AddWork(definition, execute, configure, authorize);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(definition);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>()
        where TExecutor : class
    {
        inner.AddWork<TExecutor>();
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(definition, configure);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(definition, configure, authorize);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(configure);
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(configure, authorize);
        return this;
    }
}
