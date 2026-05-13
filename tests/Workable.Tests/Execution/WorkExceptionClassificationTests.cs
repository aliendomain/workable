using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "ExceptionClassification")]
public sealed class WorkExceptionClassificationTests
{
    [Fact]
    public async Task WorkLevelTransientClassificationRunsFirstAndStopsTheChain()
    {
        var systemClassifierCalled = false;
        var globalClassifierCalled = false;
        var (completion, _) = await RunThrowingWork(
            services => services.AddWorkable(builder => builder.ClassifyExceptions(_ =>
            {
                globalClassifierCalled = true;
                return WorkExceptionClassification.Transient;
            })),
            system => system.ClassifyExceptions(_ =>
            {
                systemClassifierCalled = true;
                return WorkExceptionClassification.Transient;
            }),
            work =>
            {
                work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                work.ClassifyExceptions(_ => WorkExceptionClassification.Transient);
            });

        AssertExceptionClassification(completion, WorkExceptionClassification.Transient, expectedTransient: true);
        Assert.False(systemClassifierCalled);
        Assert.False(globalClassifierCalled);
    }

    [Fact]
    public async Task WorkLevelNonTransientClassificationRunsFirstAndStopsTheChain()
    {
        var systemClassifierCalled = false;
        var globalClassifierCalled = false;
        var (completion, _) = await RunThrowingWork(
            services => services.AddWorkable(builder => builder.ClassifyExceptions(_ =>
            {
                globalClassifierCalled = true;
                return WorkExceptionClassification.Transient;
            })),
            system => system.ClassifyExceptions(_ =>
            {
                systemClassifierCalled = true;
                return WorkExceptionClassification.Transient;
            }),
            work =>
            {
                work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                work.ClassifyExceptions(_ => WorkExceptionClassification.NonTransient);
            });

        AssertExceptionClassification(completion, WorkExceptionClassification.NonTransient, expectedTransient: false);
        Assert.False(systemClassifierCalled);
        Assert.False(globalClassifierCalled);
    }

    [Fact]
    public async Task SystemLevelClassificationRunsAfterWorkLevelUnknownAndBeforeGlobal()
    {
        var globalClassifierCalled = false;
        var (completion, _) = await RunThrowingWork(
            services => services.AddWorkable(builder => builder.ClassifyExceptions(_ =>
            {
                globalClassifierCalled = true;
                return WorkExceptionClassification.NonTransient;
            })),
            system => system.ClassifyExceptions(_ => WorkExceptionClassification.Transient),
            work =>
            {
                work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                work.ClassifyExceptions(_ => WorkExceptionClassification.Unknown);
            });

        AssertExceptionClassification(completion, WorkExceptionClassification.Transient, expectedTransient: true);
        Assert.False(globalClassifierCalled);
    }

    [Fact]
    public async Task GlobalClassificationRunsAfterWorkAndSystemUnknown()
    {
        var (completion, _) = await RunThrowingWork(
            services => services.AddWorkable(builder => builder.ClassifyExceptions(_ => WorkExceptionClassification.Transient)),
            system => system.ClassifyExceptions(_ => WorkExceptionClassification.Unknown),
            work =>
            {
                work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                work.ClassifyExceptions(_ => WorkExceptionClassification.Unknown);
            });

        AssertExceptionClassification(completion, WorkExceptionClassification.Transient, expectedTransient: true);
    }

    [Fact]
    public async Task UnknownClassificationFromEveryLevelIsNotTransient()
    {
        var (completion, _) = await RunThrowingWork(
            services => services.AddWorkable(builder => builder.ClassifyExceptions(_ => WorkExceptionClassification.Unknown)),
            system => system.ClassifyExceptions(_ => WorkExceptionClassification.Unknown),
            work =>
            {
                work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                work.ClassifyExceptions(_ => WorkExceptionClassification.Unknown);
            });

        AssertExceptionClassification(completion, WorkExceptionClassification.Unknown, expectedTransient: false);
    }

    [Fact]
    public async Task AppWideClassificationAppliesToEverySystem()
    {
        var services = new ServiceCollection()
            .AddWorkable(builder => builder.ClassifyExceptions(_ => WorkExceptionClassification.Transient))
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("default-throws", "Throws."),
                ThrowingWork,
                work => work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled)))
            .AddWorkableSystem("named", builder => builder.AddWork(
                WorkDefinition.Create("named-throws", "Throws."),
                ThrowingWork,
                work => work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled)));
        var registry = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("named", out var named));

        await registry.Default.Start();
        await named.Start();

        var defaultCompletion = await (await registry.Default.Queue.Enqueue("default-throws")).WaitForCompletion();
        var namedCompletion = await (await named.Queue.Enqueue("named-throws")).WaitForCompletion();

        AssertExceptionClassification(defaultCompletion, WorkExceptionClassification.Transient, expectedTransient: true);
        AssertExceptionClassification(namedCompletion, WorkExceptionClassification.Transient, expectedTransient: true);
    }

    [Fact]
    public async Task FeatureWorkClassificationFlowsThroughAddWorkableWork()
    {
        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .AddWorkableWork(
                WorkDefinition.Create("feature-throws", "Throws."),
                ThrowingWork,
                work =>
                {
                    work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                    work.ClassifyExceptions(_ => WorkExceptionClassification.Transient);
                });
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("feature-throws")).WaitForCompletion();

        AssertExceptionClassification(completion, WorkExceptionClassification.Transient, expectedTransient: true);
    }

    [Fact]
    public async Task UnhandledExecutionExceptionIsLogged()
    {
        var (completion, logs) = await RunThrowingWork(
            configureServices: null,
            configureSystem: null,
            configureWork: work =>
            {
                work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled);
                work.ClassifyExceptions(_ => WorkExceptionClassification.Transient);
            });

        AssertExceptionClassification(completion, WorkExceptionClassification.Transient, expectedTransient: true);
        var log = Assert.Single(logs);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.IsType<InvalidOperationException>(log.Exception);
        Assert.Contains("failed with an unhandled exception", log.Message);
        Assert.Contains("Transient: True", log.Message);
    }

    [Fact]
    public async Task ThrowingClassifierIsLoggedAndNextClassifierCanClassify()
    {
        var (completion, logs) = await RunThrowingWork(
            configureServices: services => services.AddWorkable(builder =>
            {
                builder.ClassifyExceptions(_ => throw new InvalidOperationException("Classifier failed."));
                builder.ClassifyExceptions(_ => WorkExceptionClassification.Transient);
            }),
            configureSystem: null,
            configureWork: work => work.UseTransientRetry(WorkTransientRetryConfiguration.Disabled));

        AssertExceptionClassification(completion, WorkExceptionClassification.Transient, expectedTransient: true);
        Assert.Contains(logs, log =>
            log.Level == LogLevel.Warning &&
            log.Exception is InvalidOperationException &&
            log.Message.Contains("classifier failed", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(WorkCompletion Completion, IReadOnlyList<LogEntry> Logs)> RunThrowingWork(
        Action<IServiceCollection>? configureServices,
        Action<IWorkSystemBuilder>? configureSystem,
        Action<IWorkConfigurationBuilder>? configureWork)
    {
        var loggerFactory = new CapturingLoggerFactory();
        var definition = WorkDefinition.Create("throws", "Throws during execution.");
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        configureServices?.Invoke(services);
        services.AddWorkableSystem(builder =>
        {
            configureSystem?.Invoke(builder);
            builder.AddWork(definition, ThrowingWork, configureWork ?? (_ => { }));
        });
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        var completion = await (await system.Queue.Enqueue("throws")).WaitForCompletion();
        return (completion, loggerFactory.Logs);
    }

    private static Task<WorkExecutionResult> ThrowingWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Boom.");

    private static void AssertExceptionClassification(
        WorkCompletion completion,
        WorkExceptionClassification expectedClassification,
        bool expectedTransient)
    {
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        var message = Assert.Single(completion.Messages);
        Assert.Equal("workable.execution.exception", message.Code);
        Assert.Equal(expectedClassification.ToString(), message.Metadata?["exceptionClassification"]);
        Assert.Equal(expectedTransient, message.Metadata?["isTransient"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, message.Metadata?["exceptionType"]);
    }

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

    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
