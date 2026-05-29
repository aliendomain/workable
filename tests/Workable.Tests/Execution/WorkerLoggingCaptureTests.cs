using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Logging")]
public sealed class WorkerLoggingCaptureTests
{
    [Fact]
    public async Task CapturesExecutorAndScopedServiceLogsIntoRetainedIterationBuffer()
    {
        var loggerFactory = new CapturingLoggerFactory(isEnabled: false);
        var definition = WorkDefinition.Create("logged-work", "Captures logs from executor dependencies.");
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddScoped<LoggedDependency>();
        services.AddWorkableSystem(builder => builder.AddWork<LoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(
                level: LogLevel.Debug,
                maximumBufferedEntries: 2)));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.log"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("logged-work");
        var completion = await handle.WaitForCompletion();
        var logEvents = new[] { await ReadNext(reader), await ReadNext(reader), await ReadNext(reader) };

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.All(logEvents, workEvent => Assert.Equal("worker.log", workEvent.EventType));
        Assert.All(logEvents, workEvent => AssertLogEvent(workEvent));
        Assert.Contains(logEvents, workEvent => RequiredData(workEvent).GetProperty("log").GetProperty("message").GetString() == "dependency constructed");
        Assert.Contains(logEvents, workEvent => RequiredData(workEvent).GetProperty("log").GetProperty("message").GetString() == "executor guarded debug");
        Assert.Contains(logEvents, workEvent => RequiredData(workEvent).GetProperty("log").GetProperty("message").GetString() == "dependency executed");

        var worker = RequiredWorker(completion.Worker);
        var iteration = RequiredLastIteration(worker);
        Assert.Equal(2, iteration.Logs.Count);
        Assert.DoesNotContain(iteration.Logs, entry => entry.Message == "dependency constructed");
        Assert.Contains(iteration.Logs, entry => entry.Message == "executor guarded debug");
        Assert.Contains(iteration.Logs, entry => entry.Message == "dependency executed");
        Assert.Contains(loggerFactory.Logs, entry => entry.Message == "executor guarded debug");
        Assert.Contains(loggerFactory.Logs, entry => entry.Message == "dependency executed");
    }

    [Fact]
    public async Task DisabledLoggingDoesNotCaptureWorkerLogEventsOrBufferEntries()
    {
        var definition = WorkDefinition.Create("disabled-logging", "Does not capture worker logs.");
        var services = new ServiceCollection();
        services.AddScoped<LoggedDependency>();
        services.AddWorkableSystem(builder => builder.AddWork<LoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(isEnabled: false, level: LogLevel.Trace)));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.log"));

        var handle = await system.Queue.Enqueue("disabled-logging");
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Empty(RequiredLastIteration(RequiredWorker(completion.Worker)).Logs);
        await AssertNoEvent(subscription);
    }

    [Fact]
    public async Task LoggingLevelFiltersCapturedWorkerLogs()
    {
        var definition = WorkDefinition.Create("filtered-logging", "Captures logs at or above the configured level.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<FilteredLoggingExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Warning)));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.log"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("filtered-logging");
        var completion = await handle.WaitForCompletion();
        var warning = await ReadNext(reader);

        AssertLogEvent(warning, expectedMessage: "visible warning", expectedLevel: "Warning");
        var worker = RequiredWorker(completion.Worker);
        var iteration = RequiredLastIteration(worker);
        Assert.Single(iteration.Logs);
        Assert.Equal("visible warning", iteration.Logs[0].Message);
    }

    [Fact]
    public async Task MaximumBufferedEntriesAppliesPerRetainedIteration()
    {
        var definition = WorkDefinition.Create("retry-logged-work", "Applies log buffering per retry iteration.");
        var services = new ServiceCollection();
        services.AddSingleton<RetryAttemptCounter>();
        services.AddWorkableSystem(builder => builder.AddWork<RetryLoggedExecutor>(
            definition,
            configuration =>
            {
                configuration.ConfigureLogging(level: LogLevel.Debug, maximumBufferedEntries: 2);
                configuration.ClassifyExceptions(_ => WorkExceptionClassification.Transient);
                configuration.UseTransientRetry(WorkTransientRetryConfiguration.Default with
                {
                    Count = 1,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    Jitter = TimeSpan.Zero,
                    MaximumDelay = TimeSpan.FromMilliseconds(1),
                });
            }));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = await system.Queue.Enqueue("retry-logged-work");
        var completion = await handle.WaitForCompletion();
        var worker = RequiredWorker(completion.Worker);

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(2, worker.Iterations.Count);
        Assert.All(worker.Iterations, iteration => Assert.Equal(2, iteration.Logs.Count));
        Assert.All(worker.Iterations, iteration => Assert.DoesNotContain(iteration.Logs, entry => entry.Message.EndsWith("A")));
        Assert.All(worker.Iterations, iteration => Assert.Contains(iteration.Logs, entry => entry.Message.EndsWith("B")));
        Assert.All(worker.Iterations, iteration => Assert.Contains(iteration.Logs, entry => entry.Message.EndsWith("C")));
    }

    private static JsonElement RequiredData(WorkEvent workEvent)
        => workEvent.Data ?? throw new InvalidOperationException($"Expected data for event '{workEvent.EventType}'.");

    private static void AssertLogEvent(WorkEvent workEvent, string? expectedMessage = null, string? expectedLevel = null)
    {
        var data = RequiredData(workEvent);
        var log = data.GetProperty("log");
        var iteration = data.GetProperty("iteration");
        Assert.Equal("worker.log", workEvent.EventType);
        Assert.False(data.TryGetProperty("input", out _));
        Assert.False(data.TryGetProperty("output", out _));
        Assert.False(data.TryGetProperty("messages", out _));
        Assert.False(data.TryGetProperty("logs", out _));
        Assert.False(string.IsNullOrWhiteSpace(log.GetProperty("id").GetString()));
        Assert.True(iteration.GetProperty("sequence").GetInt64() >= 1);
        Assert.Equal("Executing", iteration.GetProperty("status").GetString());
        if (expectedMessage is not null)
        {
            Assert.Equal(expectedMessage, log.GetProperty("message").GetString());
        }

        if (expectedLevel is not null)
        {
            Assert.Equal(expectedLevel, log.GetProperty("level").GetString());
        }
    }

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    private static WorkerIterationSnapshot RequiredLastIteration(WorkerSnapshot worker)
        => worker.LastIteration ?? throw new InvalidOperationException("Expected last iteration to exist.");

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private static async Task AssertNoEvent(IWorkEventSubscription subscription)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync().AsTask());
    }

    private sealed class LoggedDependency
    {
        private readonly ILogger<LoggedDependency> logger;

        public LoggedDependency(ILogger<LoggedDependency> logger)
        {
            this.logger = logger;
            this.logger.LogWarning("dependency constructed");
        }

        public void Execute()
            => this.logger.LogInformation("dependency executed");
    }

    private sealed class LoggedExecutor(ILogger<LoggedExecutor> logger, LoggedDependency dependency) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("executor guarded debug");
            }

            dependency.Execute();
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class FilteredLoggingExecutor(ILogger<FilteredLoggingExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            logger.LogInformation("hidden information");
            logger.LogWarning("visible warning");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class RetryAttemptCounter
    {
        private int attempts;

        public int NextAttempt() => Interlocked.Increment(ref this.attempts);
    }

    private sealed class RetryLoggedExecutor(
        ILogger<RetryLoggedExecutor> logger,
        RetryAttemptCounter counter) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            var attempt = counter.NextAttempt();
            logger.LogDebug("attempt {Attempt} A", attempt);
            logger.LogDebug("attempt {Attempt} B", attempt);
            logger.LogDebug("attempt {Attempt} C", attempt);
            throw new InvalidOperationException($"transient attempt {attempt}");
        }
    }

    private sealed class CapturingLoggerFactory(bool isEnabled) : ILoggerFactory
    {
        public List<LogEntry> Logs { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName, this.Logs, isEnabled);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, List<LogEntry> logs, bool isEnabled) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => isEnabled;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logs.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
}
