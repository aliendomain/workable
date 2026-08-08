using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
/// <summary>
/// Isolates session creation and access description after authorization groups have been snapshotted.
/// </summary>
public class BaselineAuthorizationResolutionBenchmarks
{
    private WorkableBenchmarkSystem fixture = null!;

    public IEnumerable<int> DefinitionCounts => BenchmarkScales.AuthorizationDefinitionCounts;

    [ParamsSource(nameof(DefinitionCounts))]
    public int DefinitionCount { get; set; }

    [GlobalSetup]
    public void Setup()
        => this.fixture = WorkableBenchmarkSystem.CreateQueued(
                workerCount: 100,
                requiresAuthorization: true,
                definitionCount: this.DefinitionCount,
                includeUnauthorizedDefinition: true)
            .GetAwaiter()
            .GetResult();

    [Benchmark]
    public ValueTask<IWorkSystemSession> CreateSession()
        => this.fixture.System.CreateSession(this.fixture.RequestContext);

    [Benchmark]
    public ValueTask<WorkSystemAccessSummary> DescribeAccess()
        => this.fixture.System.DescribeAccess(this.fixture.RequestContext);

    [GlobalCleanup]
    public void Cleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
