using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkConfigurationBuilderShould
{
    [Fact]
    public void BuildConfigurationFromFluentShortcuts()
    {
        var builder = new WorkConfigurationBuilder(WorkConfiguration.Default);
        var fallbackPollingInterval = TimeSpan.FromSeconds(4);

        var returned = builder
            .QueueDurably(fallbackPollingInterval)
            .CompleteDurably()
            .ReturnAfterCompleted()
            .RecurEvery(TimeSpan.FromMinutes(2))
            .RetryTransientFailures(
                count: 2,
                initialDelay: TimeSpan.FromSeconds(3),
                jitter: TimeSpan.FromMilliseconds(4),
                maximumDelay: TimeSpan.FromSeconds(5),
                backoff: WorkRetryBackoff.None)
            .ConfigureLogging(isEnabled: false, LogLevel.Warning, maximumBufferedEntries: 7)
            .ConfigureRetention(purgeInterval: TimeSpan.FromSeconds(8), maximumFinalWorkers: 9)
            .LimitConcurrency(
                maximumCapacity: 3,
                scope: WorkConcurrencyScope.PerSubject,
                blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
                limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
                overrideBehavior: WorkConcurrencyOverrideBehavior.Strict)
            .RejectDuplicateSubjects(WorkIdempotencyConflictPolicy.RejectDuplicates)
            .AllowInvocationFrom(WorkInvocationChannel.Mcp, WorkInvocationChannel.SignalR);

        var configuration = builder.Build();

        Assert.Same(builder, returned);
        Assert.Equal(WorkStartPolicy.StartAndReturnAfterCompleted, configuration.Start.Policy);
        Assert.True(configuration.Coordination.IsEnabled);
        Assert.Equal(WorkCoordinationStorage.Persistent, configuration.Coordination.Storage);
        Assert.True(configuration.Coordination.Durability.IsEnabled);
        Assert.True(configuration.Coordination.Durability.CompleteDurably);
        Assert.Equal(fallbackPollingInterval, configuration.Coordination.Durability.FallbackPollingInterval);
        Assert.True(configuration.Recurrence.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(2), configuration.Recurrence.Interval);
        Assert.Equal(2, configuration.TransientRetry.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), configuration.TransientRetry.InitialDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(4), configuration.TransientRetry.Jitter);
        Assert.Equal(TimeSpan.FromSeconds(5), configuration.TransientRetry.MaximumDelay);
        Assert.Equal(WorkRetryBackoff.None, configuration.TransientRetry.Backoff);
        Assert.False(configuration.Logging.IsEnabled);
        Assert.Equal(LogLevel.Warning, configuration.Logging.Level);
        Assert.Equal(7, configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(TimeSpan.FromSeconds(8), configuration.Retention.PurgeInterval);
        Assert.Equal(9, configuration.Retention.MaximumFinalWorkers);
        Assert.True(configuration.Coordination.Concurrency.IsEnabled);
        Assert.Equal(3, configuration.Coordination.Concurrency.MaximumCapacity);
        Assert.Equal(WorkConcurrencyScope.PerSubject, configuration.Coordination.Concurrency.Scope);
        Assert.Equal(WorkConcurrencyBlockingMode.WhileExecuting, configuration.Coordination.Concurrency.BlockingMode);
        Assert.Equal(WorkConcurrencyLimitReachedBehavior.DeferStart, configuration.Coordination.Concurrency.LimitReachedBehavior);
        Assert.Equal(WorkConcurrencyOverrideBehavior.Strict, configuration.Coordination.Concurrency.OverrideBehavior);
        Assert.True(configuration.Coordination.Idempotency.IsEnabled);
        Assert.Equal(WorkIdempotencyConflictPolicy.RejectDuplicates, configuration.Coordination.Idempotency.ConflictPolicy);
        Assert.True(configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
        Assert.True(configuration.Invocation.Allows(WorkInvocationChannel.SignalR));
    }

    [Fact]
    public void BuildAuxiliaryRegistrationsFromFluentShortcuts()
    {
        var builder = new WorkConfigurationBuilder(WorkConfiguration.Default);
        WorkExceptionClassifier classifier = _ => WorkExceptionClassification.Transient;

        builder
            .ClassifyExceptions(classifier)
            .WithAutomaticStart(instanceCount: 0)
            .WithAutomaticStart(() => new StartupInput("abc"), instanceCount: 2)
            .WithInitialization<SampleInitializer>(
                WorkInitializationTiming.OnceLazy,
                executionOrder: 3);

        var classifiers = builder.BuildExceptionClassifiers();
        var starts = builder.BuildAutomaticStarts();
        var initializers = builder.BuildInitializers();

        Assert.Same(classifier, Assert.Single(classifiers));
        Assert.Equal(2, starts.Count);
        Assert.Equal(1, starts[0].InstanceCount);
        Assert.Null(starts[0].InputFactory(EmptyServices()));
        Assert.Equal(2, starts[1].InstanceCount);
        var input = starts[1].InputFactory(EmptyServices());
        Assert.NotNull(input);
        Assert.Equal(typeof(StartupInput).AssemblyQualifiedName, input.ClrType);
        Assert.Contains("abc", input.Json);
        var initializer = Assert.Single(initializers);
        Assert.Equal(typeof(SampleInitializer), initializer.InitializerType);
        Assert.Equal(WorkInitializationTiming.OnceLazy, initializer.Timing);
        Assert.Equal(3, initializer.ExecutionOrder);
    }

    [Fact]
    public void RejectNullRequiredConfigurationObjectsAndDelegates()
    {
        var builder = new WorkConfigurationBuilder(WorkConfiguration.Default);

        Assert.Throws<ArgumentNullException>(() => builder.UseStart(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseCoordination(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseRecurrence(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseTransientRetry(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseLogging(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseRetention(null!));
        Assert.Throws<ArgumentNullException>(() => builder.UseInvocation(null!));
        Assert.Throws<ArgumentNullException>(() => builder.WithAutomaticStart<object>(null!));
        Assert.Throws<ArgumentNullException>(() => builder.ClassifyExceptions(null!));
    }

    private static IServiceProvider EmptyServices()
        => new ServiceCollection().BuildServiceProvider();

    private sealed record StartupInput(string Value);

    private sealed class SampleInitializer : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
