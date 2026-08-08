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
    private WorkRequestContext mismatchedRequestContext = null!;

    public IEnumerable<int> DefinitionCounts => BenchmarkScales.AuthorizationDefinitionCounts;

    [ParamsSource(nameof(DefinitionCounts))]
    public int DefinitionCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        this.fixture = WorkableBenchmarkSystem.CreateQueued(
                workerCount: 100,
                requiresAuthorization: true,
                definitionCount: this.DefinitionCount,
                includeUnauthorizedDefinition: true)
            .GetAwaiter()
            .GetResult();
        var authorization = this.fixture.RequestContext.Authorization
            ?? throw new InvalidOperationException("Expected a benchmark authorization snapshot.");
        this.mismatchedRequestContext = this.fixture.RequestContext with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                "foreign-system",
                authorization.Actor,
                authorization.Groups,
                readableDefinitionIds: null),
        };
    }

    [Benchmark]
    public ValueTask<IWorkSystemSession> CreateSession()
        => this.fixture.System.CreateSession(this.fixture.RequestContext);

    [Benchmark]
    public ValueTask<IWorkSystemSession> CreateSessionWithMismatchedSnapshot()
        => this.fixture.System.CreateSession(this.mismatchedRequestContext);

    [Benchmark]
    public ValueTask<WorkSystemAccessSummary> DescribeAccess()
        => this.fixture.System.DescribeAccess(this.fixture.RequestContext);

    [GlobalCleanup]
    public void Cleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
