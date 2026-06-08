using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkSystemBuilder
{
    IWorkSystemBuilder WithWorkDefaults(
        Action<IWorkDefinitionBuilder> register,
        Action<IWorkConfigurationBuilder>? configure = null,
        Action<IWorkAuthorizationBuilder>? authorize = null);

    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute);

    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute);

    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute);

    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize);

    IWorkSystemBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>()
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class;

    IWorkSystemBuilder AddWorkDefinitionSource<TSource>()
        where TSource : class, IWorkDefinitionSource;

    IWorkSystemBuilder AddWorkDefinitionSource<TSource>(Func<IServiceProvider, TSource> sourceFactory)
        where TSource : class, IWorkDefinitionSource;

    IWorkSystemBuilder AddStartupWorkSource<TSource>()
        where TSource : class, IStartupWorkSource;

    IWorkSystemBuilder AddStartupWorkSource<TSource>(Func<IServiceProvider, TSource> sourceFactory)
        where TSource : class, IStartupWorkSource;

    IWorkSystemBuilder IncludeContributedWork(bool enabled = true);

    IWorkSystemBuilder RequireAuthorization(bool required = true);

    IWorkSystemBuilder ConfigureAuthorization(Action<IWorkSystemAuthorizationBuilder> configure);

    IWorkSystemBuilder StartWithHost(bool enabled = true);

    IWorkSystemBuilder UseShutdownGracePeriod(TimeSpan gracePeriod);

    IWorkSystemBuilder UseShutdownGracePeriodRatio(double hostShutdownTimeoutRatio);

    IWorkSystemBuilder UseRetention(WorkSystemRetentionConfiguration retention);

    IWorkSystemBuilder ConfigureRetention(int? maximumFinalWorkers = null);

    IWorkSystemBuilder UseCapacity(WorkSystemCapacityConfiguration capacity);

    IWorkSystemBuilder ConfigureCapacity(int? maximumWorkers = null);

    IWorkSystemBuilder ClassifyExceptions(WorkExceptionClassifier classifier);

}
