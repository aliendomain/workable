namespace Workable;

public interface IWorkDefinitionBuilder
{
    IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute);

    IWorkDefinitionBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute);

    IWorkDefinitionBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute);

    IWorkDefinitionBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkDefinitionBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class;

    IWorkDefinitionBuilder AddWork<TExecutor>()
        where TExecutor : class;

    IWorkDefinitionBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    IWorkDefinitionBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;
}
