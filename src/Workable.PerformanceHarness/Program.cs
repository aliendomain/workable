using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Workable;

var options = HarnessOptions.Parse(args);
if (options.ShowHelp)
{
    HarnessOptions.PrintHelp();
    return 0;
}

Console.WriteLine("Workable performance harness");
Console.WriteLine();
PrintOptions(options);

await using var provider = CreateProvider(options);
var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
var views = new WorkableViewQueryAdapter();
var readModelLag = new ReadModelLagTracker();

await system.Start();
try
{
    await WarmUp(system, views, options);

    var scenario = Stopwatch.StartNew();
    var fanout = RunOverviewFanout(system, views, options, readModelLag, CancellationToken.None);
    var lifecycle = await RunLifecycle(system, options, readModelLag, CancellationToken.None);
    var viewStats = await fanout;
    scenario.Stop();

    await system.Query.SystemWorkerCounts();
    readModelLag.Observe(system);
    var diagnostics = system.Diagnostics.ReadModel;

    Console.WriteLine();
    PrintLifecycle(lifecycle);
    Console.WriteLine();
    PrintFanout(viewStats);
    Console.WriteLine();
    PrintReadModel(diagnostics, readModelLag.MaxPendingUpdateCount);
    Console.WriteLine();
    Console.WriteLine($"Scenario elapsed: {FormatDuration(scenario.Elapsed)}");
}
finally
{
    await system.Stop();
}

return 0;

static ServiceProvider CreateProvider(HarnessOptions options)
{
    var even = WorkDefinition.Create("perf.lifecycle.even", category: "Perf:Even");
    var odd = WorkDefinition.Create("perf.lifecycle.odd", category: "Perf:Odd");

    return new ServiceCollection()
        .AddWorkableSystem(builder =>
        {
            builder.AddWork(even, CreateWorkExecutor(options.WorkDelay));
            builder.AddWork(odd, CreateWorkExecutor(options.WorkDelay));
        })
        .BuildServiceProvider();
}

static Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> CreateWorkExecutor(TimeSpan delay)
    => async (_, _, cancellationToken) =>
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return WorkExecutionResult.Success();
    };

static async Task WarmUp(
    IWorkSystem system,
    WorkableViewQueryAdapter views,
    HarnessOptions options)
{
    if (options.WarmupWorkers > 0)
    {
        for (var index = 0; index < options.WarmupWorkers; index++)
        {
            var handle = await system.Queue.Enqueue(
                index % 2 == 0 ? "perf.lifecycle.even" : "perf.lifecycle.odd",
                CreateInput(index));
            await handle.WaitForCompletion();
        }
    }

    var criteria = CreateViewCriteria(0, options);
    for (var index = 0; index < options.WarmupViews; index++)
    {
        await views.View(system, "overview", criteria);
    }
}

static async Task<LifecycleResult> RunLifecycle(
    IWorkSystem system,
    HarnessOptions options,
    ReadModelLagTracker readModelLag,
    CancellationToken cancellationToken)
{
    var durations = new DurationRecorder();
    var rejected = new ConcurrentBag<string>();
    var completed = 0;
    var accepted = 0;

    using var gate = new SemaphoreSlim(options.Parallelism);
    var stopwatch = Stopwatch.StartNew();
    var tasks = new List<Task>(options.Workers);

    for (var index = 0; index < options.Workers; index++)
    {
        await gate.WaitAsync(cancellationToken);
        var workerIndex = index;
        tasks.Add(Task.Run(async () =>
        {
            var workerStopwatch = Stopwatch.StartNew();
            try
            {
                var name = workerIndex % 2 == 0 ? "perf.lifecycle.even" : "perf.lifecycle.odd";
                var handle = await system.Queue.Enqueue(
                    name,
                    CreateInput(workerIndex),
                    cancellationToken: cancellationToken);
                readModelLag.Observe(system);

                if (!handle.QueueOutcome.IsAccepted)
                {
                    rejected.Add(string.Join("; ", handle.QueueOutcome.Messages.Select(message => message.Text)));
                    return;
                }

                Interlocked.Increment(ref accepted);
                await handle.WaitForCompletion(cancellationToken);
                Interlocked.Increment(ref completed);
                workerStopwatch.Stop();
                durations.Record(workerStopwatch.Elapsed);
                readModelLag.Observe(system);
            }
            finally
            {
                gate.Release();
            }
        }, cancellationToken));
    }

    await Task.WhenAll(tasks);
    stopwatch.Stop();

    return new LifecycleResult(
        options.Workers,
        accepted,
        completed,
        rejected.Count,
        stopwatch.Elapsed,
        durations.Snapshot());
}

static WorkInput CreateInput(int index)
{
    var parity = index % 2 == 0 ? "even" : "odd";
    return WorkInput.Empty
        .WithSubject(new WorkSubjectId("perf-worker", index.ToString(CultureInfo.InvariantCulture)))
        .WithIdentifier(new WorkIdentifier("batch", "performance-harness"))
        .WithIdentifier(new WorkIdentifier("parity", parity));
}

static async Task<ViewFanoutResult> RunOverviewFanout(
    IWorkSystem system,
    WorkableViewQueryAdapter views,
    HarnessOptions options,
    ReadModelLagTracker readModelLag,
    CancellationToken cancellationToken)
{
    var durations = new DurationRecorder();
    var payloadBytes = 0L;
    var calls = 0;
    var errors = 0;
    var criteria = Enumerable.Range(0, options.ViewSubscriptions)
        .Select(index => CreateViewCriteria(index, options))
        .ToArray();

    var stopwatch = Stopwatch.StartNew();
    for (var iteration = 0; iteration < options.ViewIterations; iteration++)
    {
        var tasks = criteria.Select(async viewCriteria =>
        {
            var callStopwatch = Stopwatch.StartNew();
            var result = await views.View(
                system,
                "overview",
                viewCriteria,
                cancellationToken);
            callStopwatch.Stop();

            durations.Record(callStopwatch.Elapsed);
            if (options.SerializePayloads)
            {
                Interlocked.Add(ref payloadBytes, JsonSerializer.SerializeToUtf8Bytes(result, HarnessJson.Options).Length);
            }

            if (result.Components.Values.Any(component => component.Status != "ok"))
            {
                Interlocked.Increment(ref errors);
            }

            Interlocked.Increment(ref calls);
            readModelLag.Observe(system);
        });

        await Task.WhenAll(tasks);
    }

    stopwatch.Stop();
    return new ViewFanoutResult(
        calls,
        errors,
        options.ViewSubscriptions,
        options.ViewIterations,
        options.SerializePayloads,
        payloadBytes,
        stopwatch.Elapsed,
        durations.Snapshot());
}

static WorkViewCriteria CreateViewCriteria(int subscriptionIndex, HarnessOptions options)
{
    var scope = (subscriptionIndex % 3) switch
    {
        1 => new WorkSystemCriteria(Category: "Perf:Even"),
        2 => new WorkSystemCriteria(Category: "Perf:Odd"),
        _ => null,
    };

    var profile = subscriptionIndex % 3;
    var throughputOptions = CreateThroughputOptions(options);
    WorkComponentRequest[] components = profile switch
    {
        0 =>
        [
            Component("system"),
            Component("workers", shape: WorkComponentShapes.Compact),
            Component("iterations", shape: WorkComponentShapes.Compact),
            Component("throughput", throughputOptions, WorkComponentShapes.Compact),
        ],
        1 =>
        [
            Component("system"),
            Component("workers", shape: WorkComponentShapes.Standard),
            Component("failedWorkers", shape: WorkComponentShapes.Standard),
            Component("iterations", shape: WorkComponentShapes.Standard),
            Component("failedIterations", shape: WorkComponentShapes.Standard),
            Component("completedIterations", shape: WorkComponentShapes.Standard),
            Component("throughput", throughputOptions, WorkComponentShapes.Compact),
        ],
        _ =>
        [
            Component("system"),
            Component("workers", shape: WorkComponentShapes.Standard),
            Component("failedWorkers", shape: WorkComponentShapes.Detailed),
            Component("iterations", shape: WorkComponentShapes.Standard),
            Component("failedIterations", shape: WorkComponentShapes.Detailed),
            Component("completedIterations", shape: WorkComponentShapes.Detailed),
            Component("throughput", throughputOptions, WorkComponentShapes.Standard),
        ],
    };

    return new WorkViewCriteria(scope, components);
}

static WorkComponentRequest Component(
    string type,
    JsonElement? options = null,
    string shape = WorkComponentShapes.Detailed)
    => new(type, type, options, shape);

static JsonElement CreateThroughputOptions(HarnessOptions options)
{
    var json = JsonSerializer.SerializeToElement(new
    {
        windowSeconds = options.ThroughputWindowSeconds,
        bucketSeconds = options.ThroughputBucketSeconds,
    }, HarnessJson.Options);
    return json;
}

static void PrintOptions(HarnessOptions options)
{
    Console.WriteLine("Configuration");
    Console.WriteLine($"  Workers:            {options.Workers:N0}");
    Console.WriteLine($"  Parallelism:        {options.Parallelism:N0}");
    Console.WriteLine($"  Work delay:         {options.WorkDelay.TotalMilliseconds:N0} ms");
    Console.WriteLine($"  View subscriptions: {options.ViewSubscriptions:N0}");
    Console.WriteLine($"  View iterations:    {options.ViewIterations:N0}");
    Console.WriteLine($"  Serialize payloads: {options.SerializePayloads}");
    Console.WriteLine($"  Warmup workers:     {options.WarmupWorkers:N0}");
    Console.WriteLine($"  Warmup views:       {options.WarmupViews:N0}");
}

static void PrintLifecycle(LifecycleResult result)
{
    Console.WriteLine("Lifecycle throughput");
    Console.WriteLine($"  Requested workers:  {result.RequestedWorkers:N0}");
    Console.WriteLine($"  Accepted workers:   {result.AcceptedWorkers:N0}");
    Console.WriteLine($"  Completed workers:  {result.CompletedWorkers:N0}");
    Console.WriteLine($"  Rejected workers:   {result.RejectedWorkers:N0}");
    Console.WriteLine($"  Elapsed:            {FormatDuration(result.Elapsed)}");
    Console.WriteLine($"  Completed/sec:      {Rate(result.CompletedWorkers, result.Elapsed):N1}");
    PrintDurations(result.CompletionLatency, "Completion latency");
}

static void PrintFanout(ViewFanoutResult result)
{
    Console.WriteLine("Overview view fanout");
    Console.WriteLine($"  Subscriptions:      {result.Subscriptions:N0}");
    Console.WriteLine($"  Iterations:         {result.Iterations:N0}");
    Console.WriteLine($"  View calls:         {result.ViewCalls:N0}");
    Console.WriteLine($"  Component errors:   {result.ComponentErrors:N0}");
    Console.WriteLine($"  Elapsed:            {FormatDuration(result.Elapsed)}");
    Console.WriteLine($"  Views/sec:          {Rate(result.ViewCalls, result.Elapsed):N1}");
    if (result.SerializedPayloads)
    {
        Console.WriteLine($"  Avg payload bytes:  {Average(result.TotalPayloadBytes, result.ViewCalls):N0}");
    }

    PrintDurations(result.ViewLatency, "View latency");
}

static void PrintReadModel(WorkSystemReadModelDiagnostics diagnostics, long maxPendingUpdateCount)
{
    Console.WriteLine("Read model");
    Console.WriteLine($"  Enqueued sequence:  {diagnostics.EnqueuedSequence:N0}");
    Console.WriteLine($"  Applied sequence:   {diagnostics.AppliedSequence:N0}");
    Console.WriteLine($"  Pending updates:    {diagnostics.PendingUpdateCount:N0}");
    Console.WriteLine($"  Max pending seen:   {maxPendingUpdateCount:N0}");
    Console.WriteLine($"  Applied updates:    {diagnostics.AppliedUpdateCount:N0}");
    Console.WriteLine($"  Published snapshots:{diagnostics.PublishedSnapshotCount,10:N0}");
    Console.WriteLine($"  Last batch size:    {diagnostics.LastBatchSize:N0}");
    Console.WriteLine($"  Last projection:    {diagnostics.LastProjectionDuration.TotalMilliseconds:N3} ms");
    Console.WriteLine($"  Last projected at:  {diagnostics.LastProjectedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-"}");
    Console.WriteLine($"  Projector failure:  {(diagnostics.HasProjectorFailure ? $"{diagnostics.ProjectorFailureType}: {diagnostics.ProjectorFailureMessage}" : "-")}");
}

static void PrintDurations(DurationSnapshot snapshot, string label)
{
    Console.WriteLine($"  {label}:");
    Console.WriteLine($"    Count:            {snapshot.Count:N0}");
    Console.WriteLine($"    Mean:             {snapshot.MeanMilliseconds:N3} ms");
    Console.WriteLine($"    P50:              {snapshot.P50Milliseconds:N3} ms");
    Console.WriteLine($"    P95:              {snapshot.P95Milliseconds:N3} ms");
    Console.WriteLine($"    P99:              {snapshot.P99Milliseconds:N3} ms");
    Console.WriteLine($"    Max:              {snapshot.MaxMilliseconds:N3} ms");
}

static double Rate(long count, TimeSpan elapsed)
    => elapsed.TotalSeconds <= 0 ? 0 : count / elapsed.TotalSeconds;

static double Average(long total, long count)
    => count <= 0 ? 0 : (double)total / count;

static string FormatDuration(TimeSpan duration)
    => $"{duration.TotalMilliseconds:N1} ms";

internal static class HarnessJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed record HarnessOptions(
    int Workers,
    int Parallelism,
    TimeSpan WorkDelay,
    int ViewSubscriptions,
    int ViewIterations,
    bool SerializePayloads,
    int ThroughputWindowSeconds,
    int ThroughputBucketSeconds,
    int WarmupWorkers,
    int WarmupViews,
    bool ShowHelp)
{
    public static HarnessOptions Parse(string[] args)
    {
        var options = new HarnessOptions(
            Workers: 1_000,
            Parallelism: Math.Max(1, Environment.ProcessorCount),
            WorkDelay: TimeSpan.FromMilliseconds(1),
            ViewSubscriptions: 6,
            ViewIterations: 50,
            SerializePayloads: true,
            ThroughputWindowSeconds: 60,
            ThroughputBucketSeconds: 1,
            WarmupWorkers: 20,
            WarmupViews: 3,
            ShowHelp: false);

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "-h" or "--help")
            {
                return options with { ShowHelp = true };
            }

            var value = index + 1 < args.Length
                ? args[++index]
                : throw new ArgumentException($"Missing value for '{name}'.");

            options = name switch
            {
                "--workers" => options with { Workers = PositiveInt(name, value) },
                "--parallelism" => options with { Parallelism = PositiveInt(name, value) },
                "--work-delay-ms" => options with { WorkDelay = TimeSpan.FromMilliseconds(NonNegativeInt(name, value)) },
                "--view-subscriptions" => options with { ViewSubscriptions = PositiveInt(name, value) },
                "--view-iterations" => options with { ViewIterations = PositiveInt(name, value) },
                "--serialize-payloads" => options with { SerializePayloads = Bool(name, value) },
                "--throughput-window-seconds" => options with { ThroughputWindowSeconds = PositiveInt(name, value) },
                "--throughput-bucket-seconds" => options with { ThroughputBucketSeconds = PositiveInt(name, value) },
                "--warmup-workers" => options with { WarmupWorkers = NonNegativeInt(name, value) },
                "--warmup-views" => options with { WarmupViews = NonNegativeInt(name, value) },
                _ => throw new ArgumentException($"Unknown option '{name}'. Use --help for supported options."),
            };
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Workable performance harness");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/Workable.PerformanceHarness -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --workers <n>                     Workers to queue. Default: 1000");
        Console.WriteLine("  --parallelism <n>                 Concurrent queue/wait operations. Default: processor count");
        Console.WriteLine("  --work-delay-ms <n>               Simulated work delay per worker. Default: 1");
        Console.WriteLine("  --view-subscriptions <n>          Distinct overview view criteria to fan out. Default: 6");
        Console.WriteLine("  --view-iterations <n>             Fanout loop iterations. Default: 50");
        Console.WriteLine("  --serialize-payloads <true|false> Serialize view payloads to UTF-8 JSON. Default: true");
        Console.WriteLine("  --throughput-window-seconds <n>   Throughput window requested by overview. Default: 60");
        Console.WriteLine("  --throughput-bucket-seconds <n>   Throughput bucket size requested by overview. Default: 1");
        Console.WriteLine("  --warmup-workers <n>              Workers queued before measurement. Default: 20");
        Console.WriteLine("  --warmup-views <n>                Overview views before measurement. Default: 3");
        Console.WriteLine("  -h|--help                         Show help.");
    }

    private static int PositiveInt(string name, string value)
    {
        var parsed = NonNegativeInt(name, value);
        if (parsed <= 0)
        {
            throw new ArgumentException($"'{name}' must be greater than zero.");
        }

        return parsed;
    }

    private static int NonNegativeInt(string name, string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"'{name}' must be a non-negative integer.");

    private static bool Bool(string name, string value)
        => bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{name}' must be true or false.");
}

internal sealed class DurationRecorder
{
    private readonly object gate = new();
    private readonly List<double> samples = [];

    public void Record(TimeSpan duration)
    {
        lock (this.gate)
        {
            this.samples.Add(duration.TotalMilliseconds);
        }
    }

    public DurationSnapshot Snapshot()
    {
        double[] values;
        lock (this.gate)
        {
            values = [.. this.samples];
        }

        if (values.Length == 0)
        {
            return new DurationSnapshot(0, 0, 0, 0, 0, 0);
        }

        Array.Sort(values);
        return new DurationSnapshot(
            values.Length,
            values.Average(),
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values[^1]);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

internal sealed class ReadModelLagTracker
{
    private long maxPendingUpdateCount;

    public long MaxPendingUpdateCount => Volatile.Read(ref this.maxPendingUpdateCount);

    public void Observe(IWorkSystem system)
        => UpdateMax(system.Diagnostics.ReadModel.PendingUpdateCount);

    private void UpdateMax(long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref this.maxPendingUpdateCount);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref this.maxPendingUpdateCount, value, current) == current)
            {
                return;
            }
        }
    }
}

internal sealed record DurationSnapshot(
    int Count,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds);

internal sealed record LifecycleResult(
    int RequestedWorkers,
    int AcceptedWorkers,
    int CompletedWorkers,
    int RejectedWorkers,
    TimeSpan Elapsed,
    DurationSnapshot CompletionLatency);

internal sealed record ViewFanoutResult(
    int ViewCalls,
    int ComponentErrors,
    int Subscriptions,
    int Iterations,
    bool SerializedPayloads,
    long TotalPayloadBytes,
    TimeSpan Elapsed,
    DurationSnapshot ViewLatency);
