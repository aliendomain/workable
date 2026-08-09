using System.Diagnostics;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workable.PerformanceHarness;

/// <summary>
/// Measures complete worker execution with profiling explicitly disabled versus full capture.
/// Persistence remains disabled so the result isolates profile collection and publication.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineProfilingExecutionBenchmarks
{
    private const string WorkName = "perf.profiling.execution";
    private const string ActivitySourceName = "System.Net.Http";
    private const string RequestActivityName = "System.Net.Http.HttpRequestOut";
    private const int WorkExecutionsPerInvoke = 512;
    private const int ProfileEventsPerWork = 100;
    private const int BoundedAutomaticNodeLimit = 10;

    private ProfilingExecutionBenchmarkSystem fixture = null!;
    private ProfilingExecutionBenchmarkSystem persistedFixture = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var validationFixture = ProfilingExecutionBenchmarkSystem.Create().GetAwaiter().GetResult();
        try
        {
            var off = ExecuteOne(
                validationFixture.System,
                new WorkerOptions(ProfilingEnabled: false)).GetAwaiter().GetResult();
            if (off.Worker?.Profile is not null)
            {
                throw new InvalidOperationException("The profiling-off benchmark unexpectedly captured a profile.");
            }

            var bounded = ExecuteOne(
                validationFixture.System,
                BoundedProfilingOptions()).GetAwaiter().GetResult();
            var boundedProfile = bounded.Worker?.Profile ??
                throw new InvalidOperationException("The bounded-profiling benchmark did not capture a profile.");
            if (CountNodes(boundedProfile.Root, "HTTP Request") != BoundedAutomaticNodeLimit)
            {
                throw new InvalidOperationException(
                    "The bounded-profiling benchmark did not enforce its automatic-node limit.");
            }

            var full = ExecuteOne(
                validationFixture.System,
                FullProfilingOptions()).GetAwaiter().GetResult();
            var profile = full.Worker?.Profile ??
                throw new InvalidOperationException("The full-profiling benchmark did not capture a profile.");
            if (CountNodes(profile.Root, "HTTP Request") != ProfileEventsPerWork)
            {
                throw new InvalidOperationException(
                    "The full-profiling benchmark did not capture every emitted HTTP activity.");
            }
        }
        finally
        {
            validationFixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var persistedValidationFixture = ProfilingExecutionBenchmarkSystem.Create(persistDiagnostics: true)
            .GetAwaiter().GetResult();
        try
        {
            ExecuteOne(
                persistedValidationFixture.System,
                new WorkerOptions(ProfilingEnabled: false)).GetAwaiter().GetResult();
            var persistedOff = persistedValidationFixture.Repository!.ReadCompletion().GetAwaiter().GetResult();
            if (persistedOff.Profile is not null)
            {
                throw new InvalidOperationException("The persisted profiling-off benchmark unexpectedly captured a profile.");
            }

            ExecuteOne(
                persistedValidationFixture.System,
                FullProfilingOptions()).GetAwaiter().GetResult();
            var persistedFull = persistedValidationFixture.Repository.ReadCompletion().GetAwaiter().GetResult();
            if (persistedFull.Profile is null ||
                CountNodes(persistedFull.Profile.Root, "HTTP Request") != ProfileEventsPerWork)
            {
                throw new InvalidOperationException(
                    "The persisted full-profiling benchmark did not materialize the queued profile.");
            }
        }
        finally
        {
            persistedValidationFixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture = ProfilingExecutionBenchmarkSystem.Create().GetAwaiter().GetResult();
        this.persistedFixture = ProfilingExecutionBenchmarkSystem.Create(persistDiagnostics: true)
            .GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteInstrumentationHeavyWorkWithProfilingOff()
        => this.Execute(new WorkerOptions(ProfilingEnabled: false));

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteInstrumentationHeavyWorkWithBoundedProfiling()
        => this.Execute(BoundedProfilingOptions());

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecuteInstrumentationHeavyWorkWithFullProfiling()
        => this.Execute(FullProfilingOptions());

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecutePersistedDiagnosticsWorkWithProfilingOff()
        => this.Execute(this.persistedFixture, new WorkerOptions(ProfilingEnabled: false));

    [Benchmark(OperationsPerInvoke = WorkExecutionsPerInvoke)]
    public Task<WorkCompletion> ExecutePersistedDiagnosticsWorkWithFullProfiling()
        => this.Execute(this.persistedFixture, FullProfilingOptions());

    [IterationCleanup]
    public void IterationCleanup()
    {
        this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.persistedFixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task<WorkCompletion> Execute(WorkerOptions options)
        => await this.Execute(this.fixture, options);

    private async Task<WorkCompletion> Execute(
        ProfilingExecutionBenchmarkSystem benchmarkFixture,
        WorkerOptions options)
    {
        WorkCompletion completion = default!;
        for (var index = 0; index < WorkExecutionsPerInvoke; index++)
        {
            var handle = await benchmarkFixture.System.Queue.Enqueue(
                WorkName,
                options: options);
            completion = await handle.WaitForCompletion();
        }

        return completion;
    }

    private static async Task<WorkCompletion> ExecuteOne(
        IWorkSystem system,
        WorkerOptions options)
    {
        var handle = await system.Queue.Enqueue(WorkName, options: options);
        return await handle.WaitForCompletion();
    }

    private static WorkerOptions FullProfilingOptions()
        => new()
        {
            ProfilingEnabled = true,
            ProfilingCaptureMode = WorkProfileCaptureMode.Full,
        };

    private static WorkerOptions BoundedProfilingOptions()
        => new()
        {
            ProfilingEnabled = true,
            ProfilingCaptureMode = WorkProfileCaptureMode.Bounded,
        };

    private static int CountNodes(WorkProfileSnapshotNode node, string label)
    {
        var count = string.Equals(node.Label, label, StringComparison.Ordinal) ? 1 : 0;
        foreach (var child in node.Children)
        {
            count += CountNodes(child, label);
        }

        return count;
    }

    private sealed class ProfilingExecutionBenchmarkSystem : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly ActivitySource activities;
        private readonly WorkRequestContext requestContext;

        private ProfilingExecutionBenchmarkSystem(
            ServiceProvider provider,
            IWorkSystem system,
            ActivitySource activities,
            WorkRequestContext requestContext,
            BenchmarkExecutionDiagnosticsRepository? repository)
        {
            this.provider = provider;
            this.System = system;
            this.activities = activities;
            this.requestContext = requestContext;
            this.Repository = repository;
        }

        public IWorkSystem System { get; }

        public BenchmarkExecutionDiagnosticsRepository? Repository { get; }

        public static async Task<ProfilingExecutionBenchmarkSystem> Create(bool persistDiagnostics = false)
        {
            var activities = new ActivitySource(ActivitySourceName);
            var services = new ServiceCollection();
            BenchmarkExecutionDiagnosticsRepository? repository = null;
            if (persistDiagnostics)
            {
                repository = new BenchmarkExecutionDiagnosticsRepository();
                services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
            }

            services.AddWorkableHttpClientProfiling();
            services.AddWorkableSystem(builder =>
            {
                builder
                    .RequireAuthorization(false)
                    .ConfigureProfiling(maximumAutomaticInstrumentationNodes: BoundedAutomaticNodeLimit);
                if (persistDiagnostics)
                {
                    builder.UseExecutionDiagnosticsPersistence(
                        new WorkSystemExecutionDiagnosticsPersistenceConfiguration
                        {
                            IsEnabled = true,
                            Retention = TimeSpan.FromHours(1),
                            MinimumLogLevel = LogLevel.Information,
                            ProfileCaptureMode = WorkProfileCaptureMode.Full,
                        });
                }

                builder.AddWork(
                    WorkDefinition.Create(
                        WorkName,
                        "Instrumentation-heavy work used to isolate full profiling overhead."),
                    (context, _, _) => ExecuteProfiledWork(context, activities));
            });

            var provider = services.BuildServiceProvider();
            var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
            var requestContext = BenchmarkRequestContexts.CreateAnonymous(
                "Control the profiling execution benchmark system.");
            await system.Start(requestContext);
            return new ProfilingExecutionBenchmarkSystem(
                provider,
                system,
                activities,
                requestContext,
                repository);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.System.Stop(this.requestContext);
            }
            finally
            {
                this.activities.Dispose();
                await this.provider.DisposeAsync();
            }
        }

        private static Task<WorkExecutionResult> ExecuteProfiledWork(
            IWorkExecutionContext context,
            ActivitySource activities)
        {
            for (var index = 0; index < ProfileEventsPerWork; index++)
            {
                using var step = context.Profile.StartTiming("Application step", index);
                context.Profile.AddInfo("Application result", index);
                using var activity = activities.StartActivity(
                    RequestActivityName,
                    ActivityKind.Client,
                    default(ActivityContext),
                    [
                        new KeyValuePair<string, object?>("http.request.method", "GET"),
                        new KeyValuePair<string, object?>(
                            "url.full",
                            $"https://example.test/orders/{index}?include=items"),
                    ]);
                activity?.SetTag("http.response.status_code", 200);
            }

            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    public sealed class BenchmarkExecutionDiagnosticsRepository : IWorkExecutionDiagnosticsRepository
    {
        private readonly Channel<WorkExecutionDiagnosticIterationCompletion> completions =
            Channel.CreateUnbounded<WorkExecutionDiagnosticIterationCompletion>();

        public Task Initialize(
            WorkExecutionDiagnosticsInitializationContext context,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BeginIteration(
            WorkExecutionDiagnosticIterationStart iteration,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendLogs(
            IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CompleteIteration(
            WorkExecutionDiagnosticIterationCompletion completion,
            CancellationToken cancellationToken = default)
        {
            this.completions.Writer.TryWrite(completion);
            return Task.CompletedTask;
        }

        public Task<int> DeleteExpired(
            WorkExecutionDiagnosticsExpirationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<WorkExecutionDiagnosticQueryResult> Query(
            WorkExecutionDiagnosticCriteria criteria,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkExecutionDiagnosticQueryResult([]));

        public Task<WorkExecutionDiagnosticArtifact?> Get(
            WorkExecutionDiagnosticGetRequest request,
            CancellationToken cancellationToken = default)
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

        public async Task<WorkExecutionDiagnosticIterationCompletion> ReadCompletion()
            => await this.completions.Reader.ReadAsync();
    }
}
