using BenchmarkDotNet.Attributes;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks persistence-backed idempotency acceptance, duplicate rejection, and contention-heavy duplicate submission.
/// </summary>
public class BaselineIdempotencyBenchmarks
{
    private DurableWorkBenchmarkSystem fixture = null!;
    private int nextSubjectIndex;

    public IEnumerable<int> ParallelismValues => BenchmarkScales.IdempotencyParallelism;

    [ParamsSource(nameof(ParallelismValues))]
    public int Parallelism { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture = DurableWorkBenchmarkSystem.Create().GetAwaiter().GetResult();
        this.nextSubjectIndex = 0;
    }

    [Benchmark(Baseline = true)]
    public Task<IWorkerHandle> AcceptNewPersistentIdempotencyReservation()
        => this.fixture.Session.Queue.Enqueue(
            this.fixture.PersistentIdempotentWorkName,
            CreateSubjectInput(this.nextSubjectIndex++));

    [Benchmark]
    public async Task<WorkQueueStatus> RejectDuplicatePersistentIdempotencyReservation()
    {
        var subjectIndex = this.nextSubjectIndex++;
        await this.fixture.Session.Queue.Enqueue(
            this.fixture.PersistentIdempotentWorkName,
            CreateSubjectInput(subjectIndex));
        var duplicate = await this.fixture.Session.Queue.Enqueue(
            this.fixture.PersistentIdempotentWorkName,
            CreateSubjectInput(subjectIndex));
        return duplicate.QueueOutcome.Status;
    }

    [Benchmark]
    public async Task<int> RejectDuplicateDurableSubjectsUnderContention()
    {
        var subjectIndex = this.nextSubjectIndex++;
        var attempts = await Task.WhenAll(
            Enumerable.Range(0, this.Parallelism)
                .Select(_ => this.fixture.Session.Queue.Enqueue(
                    this.fixture.DurableIdempotentWorkName,
                    CreateSubjectInput(subjectIndex))));
        return attempts.Count(handle => !handle.QueueOutcome.IsAccepted);
    }

    [IterationCleanup]
    public void IterationCleanup()
        => this.fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static WorkInput CreateSubjectInput(int subjectIndex)
        => WorkableBenchmarkSystem.CreateInput(subjectIndex)
            .WithSubject(new WorkSubjectId("perf-order", $"subject-{subjectIndex:D6}"));
}
