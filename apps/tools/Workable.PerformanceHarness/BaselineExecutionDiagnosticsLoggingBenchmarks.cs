using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workable.PerformanceHarness;

/// <summary>
/// Measures the synchronous work-path cost of stable, bounded persistent log capture.
/// Repository writes remain on the diagnostics writer and are not part of work completion latency.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineExecutionDiagnosticsLoggingBenchmarks
{
    private const string WorkName = "perf.execution-diagnostics.logging";
    private const int WorkExecutionsPerInvoke = 256;
    private const int LogsPerWork = 100;
    private static readonly WorkRequestContext LifecycleContext =
        WorkRequestContext.Create(WorkInvocationChannel.InProcess);

    private LoggingFixture off = null!;
    private LoggingFixture captured = null!;
    private LoggingFixture bounded = null!;
    private LoggingFixture saturated = null!;

    [IterationSetup]
    public void IterationSetup()
    {
        this.off = LoggingFixture.Create(LoggingMode.Off).GetAwaiter().GetResult();
        this.captured = LoggingFixture.Create(LoggingMode.Captured).GetAwaiter().GetResult();
        this.bounded = LoggingFixture.Create(LoggingMode.Bounded).GetAwaiter().GetResult();
        this.saturated = LoggingFixture.Create(LoggingMode.Saturated).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteLoggingWorkWithPersistenceOff()
        => Execute(this.off);

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteLoggingWorkWithPersistentCapture()
        => Execute(this.captured);

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteLoggingWorkAfterArtifactBound()
        => Execute(this.bounded);

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteLoggingWorkWithSaturatedWriter()
        => Execute(this.saturated);

    [IterationCleanup]
    public void IterationCleanup()
    {
        this.saturated.Repository?.ReleaseWrites();
        this.off.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.captured.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.bounded.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.saturated.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task<WorkCompletion> Execute(LoggingFixture fixture)
    {
        WorkCompletion completion = default!;
        for (var index = 0; index < WorkExecutionsPerInvoke; index++)
        {
            completion = await (await fixture.System.Queue.Enqueue(WorkName)).WaitForCompletion();
        }

        return completion;
    }

    private enum LoggingMode
    {
        Off,
        Captured,
        Bounded,
        Saturated,
    }

    private sealed class LoggingFixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private LoggingFixture(
            ServiceProvider provider,
            IWorkSystem system,
            LoggingDiagnosticsRepository? repository)
        {
            this.provider = provider;
            this.System = system;
            this.Repository = repository;
        }

        public IWorkSystem System { get; }

        public LoggingDiagnosticsRepository? Repository { get; }

        public static async Task<LoggingFixture> Create(LoggingMode mode)
        {
            var services = new ServiceCollection();
            LoggingDiagnosticsRepository? repository = null;
            if (mode != LoggingMode.Off)
            {
                repository = new LoggingDiagnosticsRepository(mode == LoggingMode.Saturated);
                services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
            }

            services.AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization(false);
                if (mode != LoggingMode.Off)
                {
                    builder.UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
                    {
                        IsEnabled = true,
                        MinimumLogLevel = LogLevel.Information,
                        Retention = TimeSpan.FromHours(1),
                        ChannelCapacity = mode == LoggingMode.Saturated ? 8 : 100_000,
                        ControlOperationCapacity = mode == LoggingMode.Saturated ? 512 : 10_000,
                        LogBatchSize = mode == LoggingMode.Saturated ? 4 : 250,
                        MaximumLogsPerIteration = mode == LoggingMode.Bounded ? 10 : LogsPerWork,
                        MaximumPendingLogBytes = 256 * 1024 * 1024,
                    });
                }

                builder.AddWork(
                    WorkDefinition.Create(WorkName),
                    (context, _, _) =>
                    {
                        var logger = context.Services.GetRequiredService<ILogger<BaselineExecutionDiagnosticsLoggingBenchmarks>>();
                        for (var index = 0; index < LogsPerWork; index++)
                        {
                            logger.LogInformation(
                                "Processed item {ItemIndex} for {Tenant}",
                                index,
                                "benchmark-tenant");
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    });
            });

            var provider = services.BuildServiceProvider();
            var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
            await system.Start(LifecycleContext);
            return new LoggingFixture(provider, system, repository);
        }

        public async ValueTask DisposeAsync()
        {
            this.Repository?.ReleaseWrites();
            await this.System.Stop(LifecycleContext);
            await this.provider.DisposeAsync();
        }
    }

    private sealed class LoggingDiagnosticsRepository(bool blockWrites) : IWorkExecutionDiagnosticsRepository
    {
        private readonly TaskCompletionSource writeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkExecutionDiagnosticsInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BeginIteration(WorkExecutionDiagnosticIterationStart iteration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task AppendLogs(IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs, CancellationToken cancellationToken = default)
        {
            if (blockWrites)
            {
                await this.writeRelease.Task.WaitAsync(cancellationToken);
            }
        }

        public Task CompleteIteration(WorkExecutionDiagnosticIterationCompletion completion, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> DeleteExpired(WorkExecutionDiagnosticsExpirationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<WorkExecutionDiagnosticQueryResult> Query(WorkExecutionDiagnosticCriteria criteria, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkExecutionDiagnosticQueryResult([]));

        public Task<WorkExecutionDiagnosticArtifact?> Get(WorkExecutionDiagnosticGetRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkExecutionDiagnosticArtifact?>(null);

        public Task<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>> ListCaptureRules(
            WorkExecutionDiagnosticsInitializationContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>>([]);

        public Task UpsertCaptureRule(
            WorkExecutionDiagnosticCaptureRule rule,
            int maximumActiveRules,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> DeleteCaptureRule(
            WorkExecutionDiagnosticCaptureRuleDeleteRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public void ReleaseWrites() => this.writeRelease.TrySetResult();
    }
}
