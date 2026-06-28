using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Running;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workable;
using Workable.PerformanceHarness;
using Workable.SqlServer;

if (TryRunBenchmarks(args, out var benchmarkExitCode))
{
    return benchmarkExitCode;
}

var options = HarnessOptions.Parse(args);
if (options.ShowHelp)
{
    HarnessOptions.PrintHelp();
    return 0;
}

var runTimestampUtc = DateTimeOffset.UtcNow;
 (string ConnectionString, string Description)? resolvedDurability = options.QueueMode.IsDurable()
    ? await ResolveDurability(options)
    : null;

Console.WriteLine("Workable performance harness");
Console.WriteLine();
PrintOptions(options, resolvedDurability?.Description);

if (!options.Scenario.Equals("lifecycle-fanout", StringComparison.OrdinalIgnoreCase))
{
    var scenarioMetrics = await ScenarioBenchmarkSuite.Run(options);
    WriteCsvIfRequested(options, runTimestampUtc, scenarioMetrics);
    return 0;
}

if (options.QueueMode.IsDurable())
{
    await PrepareDurabilityStore(
        resolvedDurability?.ConnectionString ?? options.DurabilityConnectionString,
        options.DurabilitySchemaName,
        options.DurabilityResetStore);
}

await using var provider = CreateProvider(
    options,
    resolvedDurability?.ConnectionString ?? options.DurabilityConnectionString);
var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
var views = new WorkableViewQueryAdapter();
var readModelLag = new ReadModelLagTracker();
var lifecycleContext = WorkRequestContext.Create(
    WorkInvocationChannel.InProcess,
    new WorkActor(Id: "performance-harness", Name: "Performance Harness"),
    "Control Workable performance harness.");

await system.Start(lifecycleContext);
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

    WriteCsvIfRequested(
        options,
        runTimestampUtc,
        CreateLifecycleFanoutCsvMetrics(
            options,
            lifecycle,
            viewStats,
            diagnostics,
            readModelLag.MaxPendingUpdateCount,
            scenario.Elapsed));
}
finally
{
    await system.Stop(lifecycleContext);
}

return 0;

static bool TryRunBenchmarks(string[] args, out int exitCode)
{
    exitCode = 0;
    if (args.Length == 0 || args[0] is not ("--benchmark" or "--benchmarks"))
    {
        return false;
    }

    var benchmarkArgs = args[1..];
    if (benchmarkArgs.Length == 0)
    {
        benchmarkArgs = ["--filter", "*Baseline*"];
    }

    BenchmarkSwitcher
        .FromAssembly(typeof(BaselineWorkerQueryBenchmarks).Assembly)
        .Run(benchmarkArgs);
    return true;
}

static ServiceProvider CreateProvider(HarnessOptions options, string durabilityConnectionString)
{
    var even = WorkDefinition.Create("perf.lifecycle.even", category: "Perf:Even");
    var odd = WorkDefinition.Create("perf.lifecycle.odd", category: "Perf:Odd");
    var services = new ServiceCollection();
    if (options.QueueMode.IsDurable())
    {
        services.AddWorkableSqlServerDurableQueue(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = durabilityConnectionString,
            SchemaName = options.DurabilitySchemaName,
            EnqueueBatchSize = options.DurableEnqueueBatchSize,
            EnqueueBatchWindow = TimeSpan.FromMilliseconds(options.DurableEnqueueBatchWindowMs),
            ClaimBatchSize = options.DurableClaimBatchSize,
            RecentClaimSampleCapacity = options.DurableClaimSampleCapacity,
        });
    }

    return services
        .AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(even, CreateWorkExecutor(options.WorkDelay), ConfigureQueueMode(options.QueueMode));
            builder.AddWork(odd, CreateWorkExecutor(options.WorkDelay), ConfigureQueueMode(options.QueueMode));
        })
        .BuildServiceProvider();
}

static Action<IWorkConfigurationBuilder> ConfigureQueueMode(HarnessQueueMode queueMode)
    => queueMode switch
    {
        HarnessQueueMode.InMemory => _ => { },
        HarnessQueueMode.DurableIdempotent => configuration => configuration
            .QueueDurably()
            .RejectDuplicateSubjects(),
        HarnessQueueMode.DurableNonIdempotent => configuration => configuration.QueueDurably(),
        _ => throw new ArgumentOutOfRangeException(nameof(queueMode), queueMode, "Unknown queue mode."),
    };

static async Task PrepareDurabilityStore(
    string connectionString,
    string schemaName,
    bool resetStore)
{
    await EnsureDatabase(connectionString);
    await WorkableSqlServerSchema.Apply(
        connectionString,
        schemaName);

    if (!resetStore)
    {
        return;
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        $"""
DELETE FROM {QuoteIdentifier(schemaName)}.[WorkflowRuns];
DELETE FROM {QuoteIdentifier(schemaName)}.[WorkQueueEntries];
DELETE FROM {QuoteIdentifier(schemaName)}.[WorkEntries];
""";
    await command.ExecuteNonQueryAsync();
}

static async Task<(string ConnectionString, string Description)> ResolveDurability(HarnessOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.DurabilityConnectionString))
    {
        return (options.DurabilityConnectionString, "explicit connection string");
    }

    var sql = await BenchmarkSqlServerEnvironment.GetShared();
    return (sql.ConnectionString, sql.Description);
}

static async Task EnsureDatabase(string connectionString)
{
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
    {
        return;
    }

    var databaseName = builder.InitialCatalog;
    builder.InitialCatalog = "master";
    await using var connection = new SqlConnection(builder.ConnectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
IF DB_ID(N'{EscapeLiteral(databaseName)}') IS NULL
BEGIN
    CREATE DATABASE {QuoteIdentifier(databaseName)};
END
""";
    await command.ExecuteNonQueryAsync();
}

static string QuoteIdentifier(string identifier)
    => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

static string EscapeLiteral(string value)
    => value.Replace("'", "''", StringComparison.Ordinal);

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
    var session = system.CreateSession(CreateHarnessRequestContext());
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
        await views.View(session, "overview", criteria);
    }
}

static async Task<LifecycleResult> RunLifecycle(
    IWorkSystem system,
    HarnessOptions options,
    ReadModelLagTracker readModelLag,
    CancellationToken cancellationToken)
{
    var durations = new DurationRecorder();
    var queueDurations = new DurationRecorder();
    var rejected = new ConcurrentBag<string>();
    var acceptedWorkers = new ConcurrentBag<QueuedWorker>();
    var completed = 0;
    var accepted = 0;

    using var gate = new SemaphoreSlim(options.Parallelism);
    var stopwatch = Stopwatch.StartNew();
    var queueStopwatch = Stopwatch.StartNew();
    var tasks = new List<Task>(options.Workers);

    for (var index = 0; index < options.Workers; index++)
    {
        await gate.WaitAsync(cancellationToken);
        var workerIndex = index;
        tasks.Add(Task.Run(async () =>
        {
            var lifecycleStopwatch = Stopwatch.StartNew();
            try
            {
                var name = workerIndex % 2 == 0 ? "perf.lifecycle.even" : "perf.lifecycle.odd";
                var workerQueueStopwatch = Stopwatch.StartNew();
                var handle = await system.Queue.Enqueue(
                    name,
                    CreateInput(workerIndex),
                    cancellationToken: cancellationToken);
                workerQueueStopwatch.Stop();
                queueDurations.Record(workerQueueStopwatch.Elapsed);
                readModelLag.Observe(system);

                if (!handle.QueueOutcome.IsAccepted)
                {
                    rejected.Add(string.Join("; ", handle.QueueOutcome.Messages.Select(message => message.Text)));
                    return;
                }

                Interlocked.Increment(ref accepted);
                acceptedWorkers.Add(new QueuedWorker(handle, lifecycleStopwatch));
            }
            finally
            {
                gate.Release();
            }
        }, cancellationToken));
    }

    await Task.WhenAll(tasks);
    queueStopwatch.Stop();

    await Task.WhenAll(acceptedWorkers.Select(async worker =>
    {
        await worker.Handle.WaitForCompletion(cancellationToken);
        Interlocked.Increment(ref completed);
        worker.LifecycleStopwatch.Stop();
        durations.Record(worker.LifecycleStopwatch.Elapsed);
        readModelLag.Observe(system);
    }));
    stopwatch.Stop();

    return new LifecycleResult(
        options.Workers,
        accepted,
        completed,
        rejected.Count,
        stopwatch.Elapsed,
        queueStopwatch.Elapsed,
        queueDurations.Snapshot(),
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
    var session = system.CreateSession(CreateHarnessRequestContext());
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
                session,
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

static void PrintOptions(HarnessOptions options, string? durabilityDescription)
{
    Console.WriteLine("Configuration");
    Console.WriteLine($"  Scenario:           {options.Scenario}");
    Console.WriteLine($"  Queue mode:         {options.QueueMode.ToOptionValue()}");
    Console.WriteLine($"  Workers:            {options.Workers:N0}");
    Console.WriteLine($"  Parallelism:        {options.Parallelism:N0}");
    Console.WriteLine($"  Work delay:         {options.WorkDelay.TotalMilliseconds:N0} ms");
    Console.WriteLine($"  View subscriptions: {options.ViewSubscriptions:N0}");
    Console.WriteLine($"  View iterations:    {options.ViewIterations:N0}");
    Console.WriteLine($"  Serialize payloads: {options.SerializePayloads}");
    Console.WriteLine($"  Warmup workers:     {options.WarmupWorkers:N0}");
    Console.WriteLine($"  Warmup views:       {options.WarmupViews:N0}");
    if (!string.IsNullOrWhiteSpace(options.CsvOutputPath))
    {
        Console.WriteLine($"  CSV output:         {options.CsvOutputPath}");
    }
    if (options.QueueMode.IsDurable())
    {
        Console.WriteLine($"  SQL schema:         {options.DurabilitySchemaName}");
        Console.WriteLine($"  Reset durable rows: {options.DurabilityResetStore}");
        Console.WriteLine($"  SQL batch size:     {options.DurableEnqueueBatchSize:N0}");
        Console.WriteLine($"  SQL batch window:   {options.DurableEnqueueBatchWindowMs:N0} ms");
        Console.WriteLine($"  SQL claim batch:    {options.DurableClaimBatchSize:N0}");
        Console.WriteLine($"  SQL claim samples:  {options.DurableClaimSampleCapacity:N0}");
        Console.WriteLine($"  SQL host:           {durabilityDescription ?? "explicit connection string"}");
    }
}

static void PrintLifecycle(LifecycleResult result)
{
    Console.WriteLine("Lifecycle throughput");
    Console.WriteLine($"  Requested workers:  {result.RequestedWorkers:N0}");
    Console.WriteLine($"  Accepted workers:   {result.AcceptedWorkers:N0}");
    Console.WriteLine($"  Completed workers:  {result.CompletedWorkers:N0}");
    Console.WriteLine($"  Rejected workers:   {result.RejectedWorkers:N0}");
    Console.WriteLine($"  Elapsed:            {FormatDuration(result.Elapsed)}");
    Console.WriteLine($"  Queue elapsed:      {FormatDuration(result.QueueElapsed)}");
    Console.WriteLine($"  Accepted/sec:       {Rate(result.AcceptedWorkers, result.QueueElapsed):N1}");
    Console.WriteLine($"  Completed/sec:      {Rate(result.CompletedWorkers, result.Elapsed):N1}");
    PrintDurations(result.QueueLatency, "Queue latency");
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

static IReadOnlyList<HarnessMetricRow> CreateLifecycleFanoutCsvMetrics(
    HarnessOptions options,
    LifecycleResult lifecycle,
    ViewFanoutResult fanout,
    WorkSystemReadModelDiagnostics diagnostics,
    long maxPendingUpdateCount,
    TimeSpan scenarioElapsed)
{
    var rows = new List<HarnessMetricRow>();
    AddIntMetric(rows, options.Scenario, "requested_workers", lifecycle.RequestedWorkers, "workers");
    AddIntMetric(rows, options.Scenario, "accepted_workers", lifecycle.AcceptedWorkers, "workers");
    AddIntMetric(rows, options.Scenario, "completed_workers", lifecycle.CompletedWorkers, "workers");
    AddIntMetric(rows, options.Scenario, "rejected_workers", lifecycle.RejectedWorkers, "workers");
    AddDoubleMetric(rows, options.Scenario, "elapsed_ms", lifecycle.Elapsed.TotalMilliseconds, "ms");
    AddDoubleMetric(rows, options.Scenario, "queue_elapsed_ms", lifecycle.QueueElapsed.TotalMilliseconds, "ms");
    AddDoubleMetric(rows, options.Scenario, "accepted_per_sec", Rate(lifecycle.AcceptedWorkers, lifecycle.QueueElapsed), "workers/sec");
    AddDoubleMetric(rows, options.Scenario, "completed_per_sec", Rate(lifecycle.CompletedWorkers, lifecycle.Elapsed), "workers/sec");
    AddDurationMetrics(rows, options.Scenario, "queue_latency", lifecycle.QueueLatency);
    AddDurationMetrics(rows, options.Scenario, "completion_latency", lifecycle.CompletionLatency);

    AddIntMetric(rows, options.Scenario, "view_calls", fanout.ViewCalls, "views");
    AddIntMetric(rows, options.Scenario, "component_errors", fanout.ComponentErrors, "errors");
    AddIntMetric(rows, options.Scenario, "subscriptions", fanout.Subscriptions, "subscriptions");
    AddIntMetric(rows, options.Scenario, "iterations", fanout.Iterations, "iterations");
    AddDoubleMetric(rows, options.Scenario, "fanout_elapsed_ms", fanout.Elapsed.TotalMilliseconds, "ms");
    AddDoubleMetric(rows, options.Scenario, "views_per_sec", Rate(fanout.ViewCalls, fanout.Elapsed), "views/sec");
    if (fanout.SerializedPayloads)
    {
        AddDoubleMetric(rows, options.Scenario, "avg_payload_bytes", Average(fanout.TotalPayloadBytes, fanout.ViewCalls), "bytes");
    }

    AddDurationMetrics(rows, options.Scenario, "view_latency", fanout.ViewLatency);
    AddLongMetric(rows, options.Scenario, "read_model_enqueued_sequence", diagnostics.EnqueuedSequence, "updates");
    AddLongMetric(rows, options.Scenario, "read_model_applied_sequence", diagnostics.AppliedSequence, "updates");
    AddLongMetric(rows, options.Scenario, "read_model_pending_updates", diagnostics.PendingUpdateCount, "updates");
    AddLongMetric(rows, options.Scenario, "read_model_max_pending_seen", maxPendingUpdateCount, "updates");
    AddLongMetric(rows, options.Scenario, "read_model_applied_updates", diagnostics.AppliedUpdateCount, "updates");
    AddLongMetric(rows, options.Scenario, "read_model_published_snapshots", diagnostics.PublishedSnapshotCount, "snapshots");
    AddLongMetric(rows, options.Scenario, "read_model_last_batch_size", diagnostics.LastBatchSize, "updates");
    AddDoubleMetric(rows, options.Scenario, "read_model_last_projection_ms", diagnostics.LastProjectionDuration.TotalMilliseconds, "ms");
    AddDoubleMetric(rows, options.Scenario, "scenario_elapsed_ms", scenarioElapsed.TotalMilliseconds, "ms");
    return rows;
}

static void AddDurationMetrics(
    ICollection<HarnessMetricRow> rows,
    string scenario,
    string prefix,
    DurationSnapshot snapshot)
{
    AddIntMetric(rows, scenario, $"{prefix}_count", snapshot.Count, "samples");
    AddDoubleMetric(rows, scenario, $"{prefix}_mean_ms", snapshot.MeanMilliseconds, "ms");
    AddDoubleMetric(rows, scenario, $"{prefix}_p50_ms", snapshot.P50Milliseconds, "ms");
    AddDoubleMetric(rows, scenario, $"{prefix}_p95_ms", snapshot.P95Milliseconds, "ms");
    AddDoubleMetric(rows, scenario, $"{prefix}_p99_ms", snapshot.P99Milliseconds, "ms");
    AddDoubleMetric(rows, scenario, $"{prefix}_max_ms", snapshot.MaxMilliseconds, "ms");
}

static void AddIntMetric(ICollection<HarnessMetricRow> rows, string scenario, string metric, int value, string unit)
    => rows.Add(new HarnessMetricRow(scenario, metric, value.ToString(CultureInfo.InvariantCulture), unit));

static void AddLongMetric(ICollection<HarnessMetricRow> rows, string scenario, string metric, long value, string unit)
    => rows.Add(new HarnessMetricRow(scenario, metric, value.ToString(CultureInfo.InvariantCulture), unit));

static void AddDoubleMetric(ICollection<HarnessMetricRow> rows, string scenario, string metric, double value, string unit)
    => rows.Add(new HarnessMetricRow(scenario, metric, value.ToString("0.###", CultureInfo.InvariantCulture), unit));

static void WriteCsvIfRequested(
    HarnessOptions options,
    DateTimeOffset runTimestampUtc,
    IReadOnlyList<HarnessMetricRow> rows)
{
    if (string.IsNullOrWhiteSpace(options.CsvOutputPath) || rows.Count == 0)
    {
        return;
    }

    var path = Path.GetFullPath(options.CsvOutputPath);
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var builder = new StringBuilder();
    builder.AppendLine("run_at_utc,scenario,metric,value,unit,queue_mode,workers,parallelism,work_delay_ms,view_subscriptions,view_iterations,serialize_payloads,throughput_window_seconds,throughput_bucket_seconds,warmup_workers,warmup_views");
    foreach (var row in rows)
    {
        AppendCsv(builder, runTimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, row.Scenario);
        builder.Append(',');
        AppendCsv(builder, row.Metric);
        builder.Append(',');
        AppendCsv(builder, row.Value);
        builder.Append(',');
        AppendCsv(builder, row.Unit);
        builder.Append(',');
        AppendCsv(builder, options.QueueMode.ToOptionValue());
        builder.Append(',');
        AppendCsv(builder, options.Workers.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.Parallelism.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.WorkDelay.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.ViewSubscriptions.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.ViewIterations.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.SerializePayloads.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.ThroughputWindowSeconds.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.ThroughputBucketSeconds.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.WarmupWorkers.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendCsv(builder, options.WarmupViews.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
    }

    File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    Console.WriteLine();
    Console.WriteLine($"CSV output:          {path}");
}

static void AppendCsv(StringBuilder builder, string? value)
{
    var text = value ?? string.Empty;
    var requiresQuotes = text.IndexOfAny([',', '"', '\r', '\n']) >= 0;
    if (!requiresQuotes)
    {
        builder.Append(text);
        return;
    }

    builder.Append('"');
    builder.Append(text.Replace("\"", "\"\"", StringComparison.Ordinal));
    builder.Append('"');
}

static WorkRequestContext CreateHarnessRequestContext()
    => WorkRequestContext.Create(
        WorkInvocationChannel.InProcess,
        actor: new WorkActor(
            Id: "workable.perf.harness",
            Name: "Workable Performance Harness"));

internal static class HarnessJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal enum HarnessQueueMode
{
    InMemory,
    DurableIdempotent,
    DurableNonIdempotent,
}

internal static class HarnessQueueModeExtensions
{
    public static bool IsDurable(this HarnessQueueMode mode)
        => mode is HarnessQueueMode.DurableIdempotent or HarnessQueueMode.DurableNonIdempotent;

    public static string ToOptionValue(this HarnessQueueMode mode)
        => mode switch
        {
            HarnessQueueMode.InMemory => "in-memory",
            HarnessQueueMode.DurableIdempotent => "durable-idempotent",
            HarnessQueueMode.DurableNonIdempotent => "durable-non-idempotent",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown queue mode."),
        };
}

internal sealed record HarnessOptions(
    string Scenario,
    HarnessQueueMode QueueMode,
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
    string DurabilityConnectionString,
    string DurabilitySchemaName,
    bool DurabilityResetStore,
    int DurableEnqueueBatchSize,
    int DurableEnqueueBatchWindowMs,
    int DurableClaimBatchSize,
    int DurableClaimSampleCapacity,
    string? CsvOutputPath,
    bool ShowHelp)
{
    public static HarnessOptions Parse(string[] args)
    {
        var options = new HarnessOptions(
            Scenario: "lifecycle-fanout",
            QueueMode: HarnessQueueMode.InMemory,
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
            DurabilityConnectionString: string.Empty,
            DurabilitySchemaName: "workable_perf",
            DurabilityResetStore: true,
            DurableEnqueueBatchSize: WorkableSqlServerQueueDurabilityOptions.DefaultEnqueueBatchSize,
            DurableEnqueueBatchWindowMs: (int)WorkableSqlServerQueueDurabilityOptions.DefaultEnqueueBatchWindow.TotalMilliseconds,
            DurableClaimBatchSize: WorkableSqlServerQueueDurabilityOptions.DefaultClaimBatchSize,
            DurableClaimSampleCapacity: 0,
            CsvOutputPath: null,
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
                "--queue-mode" => options with { QueueMode = ParseQueueMode(name, value) },
                "--scenario" => options with { Scenario = Required(name, value) },
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
                "--durability-connection-string" => options with { DurabilityConnectionString = Required(name, value) },
                "--durability-schema" => options with { DurabilitySchemaName = Required(name, value) },
                "--durability-reset-store" => options with { DurabilityResetStore = Bool(name, value) },
                "--durable-enqueue-batch-size" => options with { DurableEnqueueBatchSize = PositiveInt(name, value) },
                "--durable-enqueue-batch-window-ms" => options with { DurableEnqueueBatchWindowMs = NonNegativeInt(name, value) },
                "--durable-claim-batch-size" => options with { DurableClaimBatchSize = PositiveInt(name, value) },
                "--durable-claim-sample-capacity" => options with { DurableClaimSampleCapacity = NonNegativeInt(name, value) },
                "--csv-output" => options with { CsvOutputPath = Required(name, value) },
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
        Console.WriteLine("  dotnet run --project src/Workable.PerformanceHarness -c Release -- --benchmarks [BenchmarkDotNet options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --scenario <name>                  Scenario: lifecycle-fanout, all, queue-only, dequeue-only, start-to-completion, completion-only, mixed-queue-complete, completion-while-queue-heavy, queue-while-completion-heavy, mixed-90-10, mixed-50-50, mixed-10-90, read-model-latency, visibility-latency, index-update-cost, memory-growth, memory-release-after-purge, durable-worker-claim-isolation, durable-worker-lifecycle-breakdown, durable-memory-release-after-purge, durable-workflow-memory-recovery, event-fanout, event-delivery, event-fanout-matrix, subscription-churn, subscription-memory-release, publish-under-churn, signalr-fanout-matrix. Default: lifecycle-fanout");
        Console.WriteLine("  --queue-mode <mode>               Queue mode: in-memory, durable-idempotent, durable-non-idempotent. Default: in-memory");
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
        Console.WriteLine("  --durability-connection-string <s> SQL Server connection string for durable modes. Default: auto (WORKABLE_SQLSERVER_TEST_CONNECTION_STRING, docker, or podman)");
        Console.WriteLine("  --durability-schema <s>           SQL schema for durable modes. Default: workable_perf");
        Console.WriteLine("  --durability-reset-store <true|false> Delete durable rows before measurement. Default: true");
        Console.WriteLine("  --durable-enqueue-batch-size <n>  SQL durable enqueue microbatch size. Default: 64");
        Console.WriteLine("  --durable-enqueue-batch-window-ms <n> SQL durable enqueue microbatch window in milliseconds. Default: 1");
        Console.WriteLine("  --durable-claim-batch-size <n>    SQL durable claim batch size. Default: 7500");
        Console.WriteLine("  --durable-claim-sample-capacity <n> Keep the last n detailed durable claim samples. Default: 0");
        Console.WriteLine("  --csv-output <path>               Write scenario metrics to CSV.");
        Console.WriteLine("  --benchmarks                      Run BenchmarkDotNet baselines. Default filter: *Baseline*");
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

    private static string Required(string name, string value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"'{name}' must not be empty.")
            : value;

    private static HarnessQueueMode ParseQueueMode(string name, string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "in-memory" => HarnessQueueMode.InMemory,
            "durable-idempotent" => HarnessQueueMode.DurableIdempotent,
            "durable-non-idempotent" => HarnessQueueMode.DurableNonIdempotent,
            _ => throw new ArgumentException($"'{name}' must be in-memory, durable-idempotent, or durable-non-idempotent."),
        };
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

internal sealed record QueuedWorker(
    IWorkerHandle Handle,
    Stopwatch LifecycleStopwatch);

internal sealed record LifecycleResult(
    int RequestedWorkers,
    int AcceptedWorkers,
    int CompletedWorkers,
    int RejectedWorkers,
    TimeSpan Elapsed,
    TimeSpan QueueElapsed,
    DurationSnapshot QueueLatency,
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

internal sealed record HarnessMetricRow(
    string Scenario,
    string Metric,
    string Value,
    string Unit);
