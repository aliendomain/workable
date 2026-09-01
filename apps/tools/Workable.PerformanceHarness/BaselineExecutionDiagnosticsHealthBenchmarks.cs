using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workable.PerformanceHarness;

/// <summary>
/// Measures steady-state reads of execution-diagnostics persistence health and dynamic capabilities.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BaselineExecutionDiagnosticsHealthBenchmarks
{
    private static readonly WorkRequestContext LifecycleContext =
        WorkRequestContext.Create(WorkInvocationChannel.InProcess);

    private BenchmarkSystem notConfigured = null!;
    private BenchmarkSystem healthy = null!;
    private BenchmarkSystem unhealthy = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        this.notConfigured = await CreateSystem(repository: null);
        this.healthy = await CreateSystem(new NoopRepository());
        this.unhealthy = await CreateSystem(new FailingInitializationRepository());
    }

    [Benchmark]
    public WorkSystemExecutionDiagnosticsPersistenceDiagnostics ReadNotConfiguredStatus()
        => this.notConfigured.System.Diagnostics.ExecutionDiagnosticsPersistence;

    [Benchmark(Baseline = true)]
    public WorkSystemExecutionDiagnosticsPersistenceDiagnostics ReadHealthyStatus()
        => this.healthy.System.Diagnostics.ExecutionDiagnosticsPersistence;

    [Benchmark]
    public WorkSystemExecutionDiagnosticsPersistenceDiagnostics ReadUnhealthyStatus()
        => this.unhealthy.System.Diagnostics.ExecutionDiagnosticsPersistence;

    [Benchmark]
    public WorkSystemExecutionDiagnosticsPersistenceDiagnostics ReadUnhealthySessionStatus()
        => this.unhealthy.Session.Diagnostics.ExecutionDiagnosticsPersistence;

    [Benchmark]
    public ValueTask<IWorkSystemSession> CreateHealthySession()
        => this.healthy.System.CreateSession(LifecycleContext);

    [Benchmark]
    public ValueTask<IWorkSystemSession> CreateUnhealthySession()
        => this.unhealthy.System.CreateSession(LifecycleContext);

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await this.unhealthy.DisposeAsync();
        await this.healthy.DisposeAsync();
        await this.notConfigured.DisposeAsync();
    }

    private static async Task<BenchmarkSystem> CreateSystem(IWorkExecutionDiagnosticsRepository? repository)
    {
        var services = new ServiceCollection();
        if (repository is not null)
        {
            services.AddSingleton<IWorkExecutionDiagnosticsRepository>(repository);
        }

        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start(LifecycleContext);
        var session = await system.CreateSession(LifecycleContext);
        return new BenchmarkSystem(provider, system, session);
    }

    private sealed class BenchmarkSystem(
        ServiceProvider provider,
        IWorkSystem system,
        IWorkSystemSession session) : IAsyncDisposable
    {
        public IWorkSystem System { get; } = system;

        public IWorkSystemSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.System.Stop(LifecycleContext);
            }
            finally
            {
                await provider.DisposeAsync();
            }
        }
    }

    private class NoopRepository : IWorkExecutionDiagnosticsRepository
    {
        public virtual Task Initialize(
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
            => Task.CompletedTask;

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
    }

    private sealed class FailingInitializationRepository : NoopRepository
    {
        public override Task Initialize(
            WorkExecutionDiagnosticsInitializationContext context,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("Expected benchmark initialization failure."));
    }
}

/// <summary>
/// Measures the authorized HTTP diagnostics response that includes persistence health.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[WarmupCount(8)]
[IterationCount(6)]
public class BaselineExecutionDiagnosticsHealthHttpBenchmarks
{
    private TransportBenchmarkHost host = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        this.host = await TransportBenchmarkHost.Create();
        using var response = await this.host.Client.GetAsync("/workable/diagnostics");
        response.EnsureSuccessStatusCode();
    }

    [Benchmark]
    public async Task<System.Net.HttpStatusCode> ReadDiagnosticsHealthOverHttp()
    {
        using var response = await this.host.Client.GetAsync("/workable/diagnostics");
        return response.StatusCode;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.host.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
