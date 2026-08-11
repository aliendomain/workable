namespace Workable;

internal sealed class DefaultingWorkDefinitionBuilder(
    IWorkDefinitionBuilder inner,
    Action<IWorkConfigurationBuilder>? defaultConfigure,
    Action<IWorkAuthorizationBuilder>? defaultAuthorize) : IWorkDefinitionBuilder
{
    public IWorkDefinitionBuilder WithWorkDefaults(
        Action<IWorkDefinitionBuilder> register,
        Action<IWorkConfigurationBuilder>? configure = null,
        Action<IWorkAuthorizationBuilder>? authorize = null)
    {
        ArgumentNullException.ThrowIfNull(register);

        register(new DefaultingWorkDefinitionBuilder(
            inner,
            Compose(defaultConfigure, configure),
            Compose(defaultAuthorize, authorize)));
        return this;
    }

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        inner.AddWork(definition, execute, ComposeConfiguration(defaultConfigure, configure), Compose(defaultAuthorize, authorize));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        inner.AddWork(definition, execute, ComposeConfiguration(defaultConfigure, configure), Compose(defaultAuthorize, authorize));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder> configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        inner.AddWork(definition, execute, ComposeConfiguration(defaultConfigure, configure), Compose(defaultAuthorize, authorize));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>()
        where TExecutor : class
        => this.AddWork<TExecutor>(configure: null, authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(definition, ComposeConfiguration(defaultConfigure, configure), Compose(defaultAuthorize, authorize));
        return this;
    }

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(configure, authorize: null);

    public IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
    {
        inner.AddWork<TExecutor>(ComposeConfiguration(defaultConfigure, configure), Compose(defaultAuthorize, authorize));
        return this;
    }

    private static Action<IWorkConfigurationBuilder>? ComposeConfiguration(
        Action<IWorkConfigurationBuilder>? defaults,
        Action<IWorkConfigurationBuilder>? registration)
    {
        if (defaults is null)
        {
            return registration;
        }

        return builder =>
        {
            if (builder is not WorkConfigurationBuilder concreteBuilder)
            {
                throw new InvalidOperationException(
                    "WithWorkDefaults requires Workable's work configuration builder implementation.");
            }

            concreteBuilder.ApplyWorkDefaults(defaults);
            registration?.Invoke(builder);
        };
    }

    private static Action<TBuilder>? Compose<TBuilder>(
        Action<TBuilder>? first,
        Action<TBuilder>? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return builder =>
        {
            first(builder);
            second(builder);
        };
    }
}
