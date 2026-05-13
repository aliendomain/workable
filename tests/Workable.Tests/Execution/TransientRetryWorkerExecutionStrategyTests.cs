using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "TransientRetry")]
public sealed class TransientRetryWorkerExecutionStrategyTests
{
    [Fact]
    public async Task TransientExceptionIsRetriedAndCanSucceed()
    {
        var attempts = 0;
        var loggerFactory = new CapturingLoggerFactory();
        var system = CreateSystem(
            loggerFactory,
            (context, input, cancellationToken) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new TimeoutException("Try again.");
                }

                return Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new AttemptResult(attempts))));
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 2,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(exception => exception is TimeoutException
                    ? WorkExceptionClassification.Transient
                    : WorkExceptionClassification.Unknown));

        await system.Start();

        var completion = await (await system.Queue.Enqueue("retry-work")).WaitForCompletion();

        Assert.Equal(2, attempts);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(2, completion.Output?.ToValue<AttemptResult>()?.Attempts);
        Assert.Equal(
            [WorkCompletionStatus.Failed, WorkCompletionStatus.Completed],
            RequiredWorker(completion).Iterations.Select(iteration => iteration.Status));
        Assert.All(RequiredWorker(completion).Iterations, iteration => Assert.True(iteration.ExecutionDuration >= TimeSpan.Zero));
        var log = Assert.Single(loggerFactory.Logs);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("Retry attempt 1 of 2", log.Message);
    }

    [Fact]
    public async Task TransientRetryDelayExposesRetryingWorkerState()
    {
        var attempts = 0;
        var system = CreateSystem(
            new CapturingLoggerFactory(),
            (context, input, cancellationToken) =>
            {
                attempts++;
                throw new TimeoutException("Database is still unavailable.");
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 1,
                    initialDelay: TimeSpan.FromSeconds(30),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(exception => exception is TimeoutException
                    ? WorkExceptionClassification.Transient
                    : WorkExceptionClassification.Unknown));

        await system.Start();

        var handle = await system.Queue.Enqueue("retry-work");
        var workerId = handle.WorkerId!.Value;
        var retrying = await Eventually(async () =>
        {
            var worker = await system.Query.GetWorker(workerId);
            return worker?.State == WorkerState.Retrying ? worker : null;
        });

        Assert.Equal(1, attempts);
        Assert.NotNull(retrying.NextRunAt);
        var iteration = Assert.Single(retrying.Iterations);
        Assert.Equal(WorkCompletionStatus.Failed, iteration.Status);
        Assert.Equal("workable.execution.exception", Assert.Single(iteration.Messages).Code);

        await system.Workers.Execute(retrying.Version, WorkAction.Cancel);
        var completion = await handle.WaitForCompletion();
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task NonTransientExceptionIsNotRetried()
    {
        var attempts = 0;
        var system = CreateSystem(
            new CapturingLoggerFactory(),
            (context, input, cancellationToken) =>
            {
                attempts++;
                throw new InvalidOperationException("Do not retry.");
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 2,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(_ => WorkExceptionClassification.NonTransient));

        await system.Start();

        var completion = await (await system.Queue.Enqueue("retry-work")).WaitForCompletion();

        Assert.Equal(1, attempts);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        AssertFailureMetadata(completion, WorkExceptionClassification.NonTransient, expectedRetryAttempts: 0);
    }

    [Fact]
    public async Task UnknownExceptionIsNotRetried()
    {
        var attempts = 0;
        var system = CreateSystem(
            new CapturingLoggerFactory(),
            (context, input, cancellationToken) =>
            {
                attempts++;
                throw new InvalidOperationException("Unknown.");
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 2,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(_ => WorkExceptionClassification.Unknown));

        await system.Start();

        var completion = await (await system.Queue.Enqueue("retry-work")).WaitForCompletion();

        Assert.Equal(1, attempts);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        AssertFailureMetadata(completion, WorkExceptionClassification.Unknown, expectedRetryAttempts: 0);
    }

    [Fact]
    public async Task TransientRetryStopsAfterConfiguredRetryCount()
    {
        var attempts = 0;
        var loggerFactory = new CapturingLoggerFactory();
        var system = CreateSystem(
            loggerFactory,
            (context, input, cancellationToken) =>
            {
                attempts++;
                throw new TimeoutException("Still transient.");
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 2,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(exception => exception is TimeoutException
                    ? WorkExceptionClassification.Transient
                    : WorkExceptionClassification.Unknown));

        await system.Start();

        var completion = await (await system.Queue.Enqueue("retry-work")).WaitForCompletion();

        Assert.Equal(3, attempts);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        AssertFailureMetadata(completion, WorkExceptionClassification.Transient, expectedRetryAttempts: 2);
        Assert.Equal([LogLevel.Warning, LogLevel.Warning, LogLevel.Error], loggerFactory.Logs.Select(log => log.Level));
    }

    [Fact]
    public async Task DeclarativeFailureResultIsNotRetried()
    {
        var attempts = 0;
        var classifierCalled = false;
        var system = CreateSystem(
            new CapturingLoggerFactory(),
            (context, input, cancellationToken) =>
            {
                attempts++;
                return Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.failure", "Failed.")]));
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 2,
                    initialDelay: TimeSpan.FromMilliseconds(1),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(_ =>
                {
                    classifierCalled = true;
                    return WorkExceptionClassification.Transient;
                }));

        await system.Start();

        var completion = await (await system.Queue.Enqueue("retry-work")).WaitForCompletion();

        Assert.Equal(1, attempts);
        Assert.False(classifierCalled);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal("sample.failure", Assert.Single(completion.Messages).Code);
    }

    [Fact]
    public void RetryDelayUsesInitialDelayWhenBackoffIsNone()
    {
        var transientRetry = WorkTransientRetryConfiguration.Default with
        {
            InitialDelay = TimeSpan.FromSeconds(2),
            Jitter = TimeSpan.Zero,
            MaximumDelay = TimeSpan.FromSeconds(30),
            Backoff = WorkRetryBackoff.None,
        };

        Assert.Equal(TimeSpan.FromSeconds(2), TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempt: 1));
        Assert.Equal(TimeSpan.FromSeconds(2), TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempt: 4));
    }

    [Fact]
    public void RetryDelayUsesExponentialBackoffAndCapsAtMaximum()
    {
        var transientRetry = WorkTransientRetryConfiguration.Default with
        {
            InitialDelay = TimeSpan.FromSeconds(2),
            Jitter = TimeSpan.Zero,
            MaximumDelay = TimeSpan.FromSeconds(5),
            Backoff = WorkRetryBackoff.Exponential,
        };

        Assert.Equal(TimeSpan.FromSeconds(2), TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempt: 1));
        Assert.Equal(TimeSpan.FromSeconds(4), TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempt: 2));
        Assert.Equal(TimeSpan.FromSeconds(5), TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempt: 3));
    }

    private static IWorkSystem CreateSystem(
        ILoggerFactory loggerFactory,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure)
        => new ServiceCollection()
            .AddSingleton(loggerFactory)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("retry-work", "Retries transient exceptions."),
                execute,
                configure))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static void AssertFailureMetadata(
        WorkCompletion completion,
        WorkExceptionClassification expectedClassification,
        int expectedRetryAttempts)
    {
        var message = Assert.Single(completion.Messages);
        Assert.Equal("workable.execution.exception", message.Code);
        Assert.Equal(expectedClassification.ToString(), message.Metadata?["exceptionClassification"]);
        Assert.Equal(expectedClassification == WorkExceptionClassification.Transient, message.Metadata?["isTransient"]);
        Assert.Equal(expectedRetryAttempts, message.Metadata?["transientRetryAttempts"]);
    }

    private static WorkerSnapshot RequiredWorker(WorkCompletion completion)
        => completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");

    private static async Task<T> Eventually<T>(Func<Task<T?>> getValue)
        where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested)
        {
            if (await getValue() is { } value)
            {
                return value;
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("Condition was not reached.");
    }

    private sealed record AttemptResult(int Attempts);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<LogEntry> logs = [];

        public IReadOnlyList<LogEntry> Logs => this.logs;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(this.logs);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<LogEntry> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            logs.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
