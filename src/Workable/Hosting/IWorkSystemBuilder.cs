using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkSystemBuilder
{
    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute);

    IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute);

    IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute);

    IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder> configure);

    IWorkSystemBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>()
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class;

    IWorkSystemBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder> configure)
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

    IWorkSystemBuilder StartWithHost(bool enabled = true);

    IWorkSystemBuilder UseShutdownGracePeriod(TimeSpan gracePeriod);

    IWorkSystemBuilder ClassifyExceptions(WorkExceptionClassifier classifier);

    IWorkSystemBuilder UseDotNetOriginProvider<TProvider>()
        where TProvider : class, IDotNetWorkOriginProvider;

    IWorkSystemBuilder UseDotNetOriginProvider(Func<IServiceProvider, IDotNetWorkOriginProvider> factory);
}
