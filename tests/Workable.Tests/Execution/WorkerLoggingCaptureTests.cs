using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Logging")]
public sealed class WorkerLoggingCaptureTests
{
    [Fact]
    public async Task CapturesExecutorAndScopedServiceLogsIntoEventsAndBoundedWorkerBuffer()
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
        Assert.All(logEvents, AssertThinLogEvent);

        var worker = RequiredWorker(completion.Worker);
        Assert.Equal(2, worker.Logs.Count);
        Assert.DoesNotContain(worker.Logs, entry => entry.Message == "dependency constructed");
        Assert.Contains(worker.Logs, entry => entry.Message == "executor guarded debug");
        Assert.Contains(worker.Logs, entry => entry.Message == "dependency executed");
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
        Assert.Empty(RequiredWorker(completion.Worker).Logs);
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

        AssertThinLogEvent(warning);
        var worker = RequiredWorker(completion.Worker);
        Assert.Single(worker.Logs);
        Assert.Equal("visible warning", worker.Logs[0].Message);
    }

    private static JsonElement RequiredData(WorkEvent workEvent)
        => workEvent.Data ?? throw new InvalidOperationException($"Expected data for event '{workEvent.EventType}'.");

    private static void AssertThinLogEvent(WorkEvent workEvent)
    {
        var data = RequiredData(workEvent);
        Assert.Empty(workEvent.Messages);
        Assert.Equal("worker.log", workEvent.EventType);
        Assert.False(data.TryGetProperty("input", out _));
        Assert.False(data.TryGetProperty("output", out _));
        Assert.False(data.TryGetProperty("messages", out _));
        Assert.False(data.TryGetProperty("log", out _));
        Assert.False(data.TryGetProperty("logs", out _));
    }

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

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
