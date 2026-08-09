using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;

namespace Workable.PerformanceHarness;

/// <summary>
/// Measures stable work-start policy resolution from the execution-diagnostics rule snapshot.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class BaselineExecutionDiagnosticsPolicyBenchmarks
{
    private const int OperationsPerInvocation = 100_000;
    private const string TargetDefinition = "perf.execution-diagnostics.target";

    private WorkExecutionDiagnosticsCoordinator noRules = null!;
    private WorkExecutionDiagnosticsCoordinator unrelatedRules = null!;
    private WorkExecutionDiagnosticsCoordinator matchingRules = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.noRules = CreateCoordinator([]);
        this.unrelatedRules = CreateCoordinator(CreateRules(matching: false));
        this.matchingRules = CreateCoordinator(CreateRules(matching: true));

        _ = this.noRules.ResolvePolicy(WorkConfiguration.Default, TargetDefinition);
        _ = this.unrelatedRules.ResolvePolicy(WorkConfiguration.Default, TargetDefinition);
        _ = this.matchingRules.ResolvePolicy(WorkConfiguration.Default, TargetDefinition);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvocation)]
    public int ResolveWithoutRules()
        => Resolve(this.noRules);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int ResolveWithOneThousandUnrelatedRules()
        => Resolve(this.unrelatedRules);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int ResolveWithOneThousandMatchingRules()
        => Resolve(this.matchingRules);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.noRules.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.unrelatedRules.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.matchingRules.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static int Resolve(WorkExecutionDiagnosticsCoordinator coordinator)
    {
        WorkExecutionDiagnosticsPolicy? policy = null;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            policy = coordinator.ResolvePolicy(WorkConfiguration.Default, TargetDefinition);
        }

        return policy?.GetHashCode() ?? 0;
    }

    private static WorkExecutionDiagnosticsCoordinator CreateCoordinator(
        IReadOnlyList<WorkExecutionDiagnosticCaptureRule> rules)
    {
        var systemId = WorkSystemId.New();
        var coordinator = new WorkExecutionDiagnosticsCoordinator(
            systemId,
            "benchmark",
            new PolicyDiagnosticsRepository(rules),
            WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default,
            logger: null);
        coordinator.Initialize([], CancellationToken.None).GetAwaiter().GetResult();
        return coordinator;
    }

    private static IReadOnlyList<WorkExecutionDiagnosticCaptureRule> CreateRules(bool matching)
    {
        var now = DateTimeOffset.UtcNow;
        return [.. Enumerable.Range(0, 1_000).Select(index => new WorkExecutionDiagnosticCaptureRule(
            Guid.NewGuid(),
            WorkSystemId.New(),
            "benchmark",
            matching ? TargetDefinition : $"perf.unrelated.{index}",
            LogLevel.Critical,
            null,
            TimeSpan.FromHours(1),
            now.AddTicks(index),
            now.AddDays(1),
            new WorkActor("benchmark")))];
    }

    private sealed class PolicyDiagnosticsRepository(
        IReadOnlyList<WorkExecutionDiagnosticCaptureRule> rules) : IWorkExecutionDiagnosticsRepository
    {
        public Task Initialize(WorkExecutionDiagnosticsInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BeginIteration(WorkExecutionDiagnosticIterationStart iteration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AppendLogs(IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

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
            => Task.FromResult(rules);

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
}
