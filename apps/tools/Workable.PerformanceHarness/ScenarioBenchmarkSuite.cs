using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workable;
using Workable.PerformanceHarness;

internal static class ScenarioBenchmarkSuite
{
    private const string EvenWorkName = "perf.lifecycle.even";
    private const string OddWorkName = "perf.lifecycle.odd";
    private static readonly WorkerOptions DoNotStartOptions = new(
        Configuration: WorkConfiguration.Default with
        {
            Start = WorkStartConfiguration.DoNotStart,
        });
    private static readonly string[] AllScenarios =
    [
        "queue-only",
        "dequeue-only",
        "start-to-completion",
        "completion-only",
        "mixed-queue-complete",
        "completion-while-queue-heavy",
        "queue-while-completion-heavy",
        "mixed-90-10",
        "mixed-50-50",
        "mixed-10-90",
        "read-model-latency",
        "visibility-latency",
        "index-update-cost",
        "memory-growth",
        "memory-release-after-purge",
        "event-fanout",
        "event-delivery",
        "subscription-churn",
        "subscription-memory-release",
        "publish-under-churn",
    ];
    private static readonly string[] DurableOnlyScenarios =
    [
        "durable-memory-release-after-purge",
        "durable-workflow-memory-recovery",
    ];

    public static async Task<IReadOnlyList<HarnessMetricRow>> Run(
        HarnessOptions options,
        CancellationToken cancellationToken = default)
    {
        var scenarios = ResolveScenarios(options.Scenario);
        ValidateScenarioQueueModes(scenarios, options.QueueMode);
        var rows = new List<HarnessMetricRow>();
        Console.WriteLine();
        Console.WriteLine("Scenario benchmark results");
        Console.WriteLine("scenario\tmetric\tvalue\tunit");

        foreach (var scenario in scenarios)
        {
            var metrics = await RunScenario(scenario, options, cancellationToken);
            foreach (var metric in metrics.Items)
            {
                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{metrics.Name}\t{metric.Name}\t{metric.Value}\t{metric.Unit}"));
                rows.Add(new HarnessMetricRow(metrics.Name, metric.Name, metric.Value, metric.Unit));
            }
        }

        return rows;
    }

    private static async Task<ScenarioMetrics> RunScenario(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
        => scenario switch
        {
            "queue-only" => await RunQueueOnly(scenario, options, cancellationToken),
            "dequeue-only" => await RunDequeueOnly(scenario, options, cancellationToken),
            "start-to-completion" => await RunStartToCompletion(scenario, options, cancellationToken),
            "completion-only" => await RunCompletionOnly(scenario, options, cancellationToken),
            "mixed-queue-complete" => await RunConcurrentQueueAndComplete(
                scenario,
                queueCount: options.Workers,
                completionCount: options.Workers,
                queueFraction: 0.50,
                options,
                cancellationToken),
            "completion-while-queue-heavy" => await RunConcurrentQueueAndComplete(
                scenario,
                queueCount: options.Workers * 4,
                completionCount: options.Workers,
                queueFraction: 0.80,
                options,
                cancellationToken),
            "queue-while-completion-heavy" => await RunConcurrentQueueAndComplete(
                scenario,
                queueCount: options.Workers,
                completionCount: options.Workers * 4,
                queueFraction: 0.20,
                options,
                cancellationToken),
            "mixed-90-10" => await RunMixedRatio(scenario, queueFraction: 0.90, options, cancellationToken),
            "mixed-50-50" => await RunMixedRatio(scenario, queueFraction: 0.50, options, cancellationToken),
            "mixed-10-90" => await RunMixedRatio(scenario, queueFraction: 0.10, options, cancellationToken),
            "read-model-latency" => await RunReadModelLatency(scenario, options, cancellationToken),
            "visibility-latency" => await RunVisibilityLatency(scenario, options, cancellationToken),
            "index-update-cost" => await RunIndexUpdateCost(scenario, options, cancellationToken),
            "memory-growth" => await RunMemoryGrowth(scenario, options, cancellationToken),
            "memory-release-after-purge" => await RunMemoryReleaseAfterPurge(scenario, options, cancellationToken),
            "durable-memory-release-after-purge" => await RunDurableMemoryReleaseAfterPurge(scenario, options, cancellationToken),
            "durable-workflow-memory-recovery" => await RunDurableWorkflowMemoryRecovery(scenario, options, cancellationToken),
            "event-fanout" or "event-fanout-matrix" => await RunEventFanout(scenario, options, cancellationToken),
            "event-delivery" => await RunEventDelivery(scenario, options, cancellationToken),
            "subscription-churn" => await RunSubscriptionChurn(scenario, options, cancellationToken),
            "subscription-memory-release" => await RunSubscriptionMemoryRelease(scenario, options, cancellationToken),
            "publish-under-churn" => await RunPublishUnderChurn(scenario, options, cancellationToken),
            "signalr-fanout-matrix" => await RunSignalRFanoutMatrix(scenario, options, cancellationToken),
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario)),
        };

    private static async Task<ScenarioMetrics> RunQueueOnly(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            DoNotStartOptions,
            options,
            scenario,
            startIndex: 0,
            cancellationToken);
        var catchup = await WaitForReadModel(harness.System, cancellationToken);

        AddQueueMetrics(metrics, queued);
        AddReadModelMetrics(metrics, catchup, "post_queue");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunStartToCompletion(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var stages = new LifecycleStageRecorder(options.Workers);
        await using var harness = await HarnessSystem.Create(
            options,
            cancellationToken,
            CreateInstrumentedWorkExecutor(options.WorkDelay, stages));
        var metrics = new ScenarioMetrics(scenario);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        var lifecycle = await QueueAndWaitStartToCompletion(
            harness.System,
            options.Workers,
            options,
            scenario,
            stages,
            cancellationToken);
        stopwatch.Stop();
        var catchup = await WaitForReadModel(harness.System, cancellationToken);
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;

        metrics.Add("workers", options.Workers, "workers");
        metrics.Add("accepted_workers", lifecycle.AcceptedWorkers, "workers");
        metrics.Add("rejected_workers", lifecycle.RejectedWorkers, "workers");
        metrics.Add("completed_workers", lifecycle.CompletedWorkers, "workers");
        metrics.Add("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("completed_per_sec", Rate(lifecycle.CompletedWorkers, stopwatch.Elapsed), "workers/sec");
        metrics.Add("allocated_bytes", allocatedBytes, "bytes");
        metrics.Add("allocated_bytes_per_worker", PerWorker(allocatedBytes, lifecycle.CompletedWorkers), "bytes/worker");
        AddDurationMetrics(metrics, "queue_request_latency", lifecycle.QueueLatency);
        AddDurationMetrics(metrics, "queue_return_to_completion_observed", lifecycle.CompletionWaitLatency);
        AddDurationMetrics(metrics, "queue_start_to_executor_start", stages.SnapshotQueueStartToExecutorStart());
        AddDurationMetrics(metrics, "executor_duration", stages.SnapshotExecutorDuration());
        AddDurationMetrics(metrics, "executor_end_to_completion_observed", stages.SnapshotExecutorEndToCompletionObserved());
        AddDurationMetrics(metrics, "start_to_completion_latency", stages.SnapshotStartToCompletion());
        AddReadModelMetrics(metrics, catchup, "post_lifecycle");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunDequeueOnly(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var stages = new LifecycleStageRecorder(options.Workers);
        await using var harness = await HarnessSystem.Create(
            options,
            cancellationToken,
            CreateInstrumentedWorkExecutor(options.WorkDelay, stages));
        var metrics = new ScenarioMetrics(scenario);
        var handles = new IWorkerHandle[options.Workers];
        var queueLatencies = new DurationRecorder();
        var acceptedWorkers = 0;
        var rejectedWorkers = 0;
        var queueStopwatch = Stopwatch.StartNew();
        await RunParallel(
            options.Workers,
            options.Parallelism,
            async index =>
            {
                var queueRequestStopwatch = Stopwatch.StartNew();
                var handle = await harness.System.Queue.Enqueue(
                    WorkName(index),
                    CreateInstrumentedInput($"{scenario}-prefill", index),
                    DoNotStartOptions,
                    cancellationToken);
                queueRequestStopwatch.Stop();
                queueLatencies.Record(queueRequestStopwatch.Elapsed);
                handles[index] = handle;
                if (handle.QueueOutcome.IsAccepted)
                {
                    Interlocked.Increment(ref acceptedWorkers);
                }
                else
                {
                    Interlocked.Increment(ref rejectedWorkers);
                }
            },
            cancellationToken);
        queueStopwatch.Stop();
        var queued = new QueueOperationResult(
            [.. handles.Where(handle => handle.QueueOutcome.IsAccepted)],
            acceptedWorkers,
            rejectedWorkers,
            queueStopwatch.Elapsed,
            queueLatencies.Snapshot());
        var prefillCatchup = await WaitForReadModel(harness.System, cancellationToken);
        var versions = await GetWorkerVersions(harness.System, queued.Handles, cancellationToken);
        var startLatencies = new DurationRecorder();
        var acceptedStarts = new bool[queued.Handles.Count];

        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            queued.Handles.Count,
            options.Parallelism,
            async index =>
            {
                stages.MarkStartActionRequested(index);
                var startStopwatch = Stopwatch.StartNew();
                var outcome = await harness.System.Workers.Execute(
                    versions[index],
                    WorkAction.Start,
                    cancellationToken);
                startStopwatch.Stop();
                startLatencies.Record(startStopwatch.Elapsed);
                acceptedStarts[index] = outcome.IsAccepted;
            },
            cancellationToken);

        await RunParallel(
            queued.Handles.Count,
            options.Parallelism,
            async index =>
            {
                if (acceptedStarts[index])
                {
                    await stages.WaitForExecutorStarted(index, cancellationToken);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        await RunParallel(
            queued.Handles.Count,
            options.Parallelism,
            async index => await queued.Handles[index].WaitForCompletion(cancellationToken),
            cancellationToken);
        var catchup = await WaitForReadModel(harness.System, cancellationToken);

        metrics.Add("prefill_workers", queued.AcceptedWorkers, "workers");
        metrics.Add("accepted_starts", acceptedStarts.Count(accepted => accepted), "workers");
        metrics.Add("dequeue_elapsed_ms", stopwatch.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("dequeued_per_sec", Rate(acceptedStarts.Count(accepted => accepted), stopwatch.Elapsed), "workers/sec");
        metrics.Add("prefill_read_model_catchup_ms", prefillCatchup.Elapsed.TotalMilliseconds, "ms");
        AddDurationMetrics(metrics, "start_action_latency", startLatencies.Snapshot());
        AddDurationMetrics(metrics, "start_action_to_executor_start", stages.SnapshotStartActionToExecutorStart());
        AddReadModelMetrics(metrics, catchup, "post_dequeue");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunCompletionOnly(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            DoNotStartOptions,
            options,
            $"{scenario}-prefill",
            startIndex: 0,
            cancellationToken);
        var prefillCatchup = await WaitForReadModel(harness.System, cancellationToken);
        var versions = await GetWorkerVersions(harness.System, queued.Handles, cancellationToken);

        var completed = await CompleteWorkers(
            harness.System,
            queued.Handles,
            versions,
            options,
            cancellationToken);
        var completionCatchup = await WaitForReadModel(harness.System, cancellationToken);

        metrics.Add("prefill_workers", queued.AcceptedWorkers, "workers");
        metrics.Add("prefill_read_model_catchup_ms", prefillCatchup.Elapsed.TotalMilliseconds, "ms");
        AddCompletionMetrics(metrics, completed);
        AddReadModelMetrics(metrics, completionCatchup, "post_completion");
        return metrics;
    }

    private static Task<ScenarioMetrics> RunMixedRatio(
        string scenario,
        double queueFraction,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var queueCount = MixedQueueCount(options.Workers, queueFraction);
        return RunConcurrentQueueAndComplete(
            scenario,
            queueCount,
            completionCount: options.Workers - queueCount,
            queueFraction,
            options,
            cancellationToken);
    }

    private static async Task<ScenarioMetrics> RunConcurrentQueueAndComplete(
        string scenario,
        int queueCount,
        int completionCount,
        double queueFraction,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var completionQueue = await QueueWorkers(
            harness.System,
            completionCount,
            DoNotStartOptions,
            options,
            $"{scenario}-complete-prefill",
            startIndex: 0,
            cancellationToken);
        var prefillCatchup = await WaitForReadModel(harness.System, cancellationToken);
        var versions = await GetWorkerVersions(harness.System, completionQueue.Handles, cancellationToken);

        var mixedStopwatch = Stopwatch.StartNew();
        var completionTask = CompleteWorkers(
            harness.System,
            completionQueue.Handles,
            versions,
            options,
            cancellationToken);
        var queueTask = QueueWorkers(
            harness.System,
            queueCount,
            DoNotStartOptions,
            options,
            $"{scenario}-queue",
            startIndex: completionCount,
            cancellationToken);

        await Task.WhenAll(completionTask, queueTask);
        mixedStopwatch.Stop();
        var catchup = await WaitForReadModel(harness.System, cancellationToken);

        metrics.Add("queue_fraction", queueFraction, "ratio");
        metrics.Add("queue_workers", queueCount, "workers");
        metrics.Add("completion_workers", completionCount, "workers");
        metrics.Add("prefill_read_model_catchup_ms", prefillCatchup.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("mixed_elapsed_ms", mixedStopwatch.Elapsed.TotalMilliseconds, "ms");
        AddQueueMetrics(metrics, queueTask.Result, "mixed_");
        AddCompletionMetrics(metrics, completionTask.Result, "mixed_");
        AddReadModelMetrics(metrics, catchup, "post_mixed");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunReadModelLatency(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var queueDurations = new DurationRecorder();
        var updateLatencies = new DurationRecorder();

        for (var index = 0; index < options.Workers; index++)
        {
            var queueStopwatch = Stopwatch.StartNew();
            var handle = await harness.System.Queue.Enqueue(
                WorkName(index),
                CreateInput(scenario, index),
                DoNotStartOptions,
                cancellationToken);
            queueStopwatch.Stop();
            queueDurations.Record(queueStopwatch.Elapsed);
            if (!handle.QueueOutcome.IsAccepted)
            {
                throw new InvalidOperationException($"Read-model latency worker {index} was rejected.");
            }

            var target = harness.System.Diagnostics.ReadModel.EnqueuedSequence;
            var updateStopwatch = Stopwatch.StartNew();
            await WaitForAppliedSequence(harness.System, target, cancellationToken);
            updateStopwatch.Stop();
            updateLatencies.Record(updateStopwatch.Elapsed);
        }

        metrics.Add("workers", options.Workers, "workers");
        AddDurationMetrics(metrics, "queue_latency", queueDurations.Snapshot());
        AddDurationMetrics(metrics, "read_model_update_latency", updateLatencies.Snapshot());
        AddDiagnostics(metrics, harness.System.Diagnostics.ReadModel);
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunVisibilityLatency(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var queueDurations = new DurationRecorder();
        var visibilityLatencies = new DurationRecorder();

        for (var index = 0; index < options.Workers; index++)
        {
            var subject = CreateSubject(scenario, index);
            var queueStopwatch = Stopwatch.StartNew();
            var handle = await harness.System.Queue.Enqueue(
                WorkName(index),
                CreateInput(scenario, index, subject),
                DoNotStartOptions,
                cancellationToken);
            queueStopwatch.Stop();
            queueDurations.Record(queueStopwatch.Elapsed);
            if (!handle.QueueOutcome.IsAccepted)
            {
                throw new InvalidOperationException($"Visibility latency worker {index} was rejected.");
            }

            var visibilityStopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await harness.System.Query.Workers(
                    new WorkerCriteria(SubjectId: subject, Take: 1),
                    cancellationToken);
                if (result.TotalCount > 0)
                {
                    break;
                }

                await Task.Delay(1, cancellationToken);
            }

            visibilityStopwatch.Stop();
            visibilityLatencies.Record(visibilityStopwatch.Elapsed);
        }

        metrics.Add("workers", options.Workers, "workers");
        AddDurationMetrics(metrics, "queue_latency", queueDurations.Snapshot());
        AddDurationMetrics(metrics, "authoritative_to_read_model_visibility", visibilityLatencies.Snapshot());
        AddDiagnostics(metrics, harness.System.Diagnostics.ReadModel);
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunIndexUpdateCost(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            DoNotStartOptions,
            options,
            scenario,
            startIndex: 0,
            cancellationToken);
        var catchup = await WaitForReadModel(harness.System, cancellationToken);

        AddQueueMetrics(metrics, queued);
        AddReadModelMetrics(metrics, catchup, "index_update");
        metrics.Add(
            "index_update_queue_to_visible_ms",
            queued.Elapsed.TotalMilliseconds + catchup.Elapsed.TotalMilliseconds,
            "ms");
        metrics.Add(
            "index_update_queue_to_visible_ms_per_worker",
            queued.AcceptedWorkers == 0 ? 0 : (queued.Elapsed.TotalMilliseconds + catchup.Elapsed.TotalMilliseconds) / queued.AcceptedWorkers,
            "ms/worker");
        metrics.Add(
            "index_update_last_projection_ms_per_item",
            catchup.End.LastBatchSize == 0 ? 0 : catchup.End.LastProjectionDuration.TotalMilliseconds / catchup.End.LastBatchSize,
            "ms/item");
        metrics.Add(
            "index_update_updates_per_sec",
            Rate(catchup.AppliedUpdateDelta, catchup.Elapsed),
            "updates/sec");
        metrics.Add(
            "index_update_ms_per_worker",
            queued.AcceptedWorkers == 0 ? 0 : catchup.Elapsed.TotalMilliseconds / queued.AcceptedWorkers,
            "ms/worker");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunMemoryGrowth(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var before = CaptureMemory();

        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            DoNotStartOptions,
            options,
            scenario,
            startIndex: 0,
            cancellationToken);
        var catchup = await WaitForReadModel(harness.System, cancellationToken);

        var after = CaptureMemory();
        var managedDelta = after.ManagedBytes - before.ManagedBytes;
        var privateDelta = after.PrivateBytes - before.PrivateBytes;

        AddQueueMetrics(metrics, queued);
        AddReadModelMetrics(metrics, catchup, "post_memory_growth");
        AddMemoryMetrics(metrics, "memory_before", before);
        AddMemoryMetrics(metrics, "memory_after", after);
        metrics.Add("managed_memory_delta_bytes", managedDelta, "bytes");
        metrics.Add("managed_memory_delta_per_worker_bytes", PerWorker(managedDelta, queued.AcceptedWorkers), "bytes/worker");
        metrics.Add("private_memory_delta_bytes", privateDelta, "bytes");
        metrics.Add("private_memory_delta_per_worker_bytes", PerWorker(privateDelta, queued.AcceptedWorkers), "bytes/worker");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunMemoryReleaseAfterPurge(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var before = CaptureMemory();

        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            DoNotStartOptions,
            options,
            scenario,
            startIndex: 0,
            cancellationToken);
        var queueCatchup = await WaitForReadModel(harness.System, cancellationToken);
        var queuedVersions = await GetWorkerVersions(harness.System, queued.Handles, cancellationToken);
        var completed = await CompleteWorkers(
            harness.System,
            queued.Handles,
            queuedVersions,
            options,
            cancellationToken);
        var completionCatchup = await WaitForReadModel(harness.System, cancellationToken);
        var afterCompletion = CaptureMemory();

        var purged = await ExecuteWorkerAction(
            harness.System,
            queued.Handles,
            WorkAction.Purge,
            options,
            cancellationToken);
        var purgeCatchup = await WaitForReadModel(harness.System, cancellationToken);
        var afterPurge = CaptureMemory();

        AddQueueMetrics(metrics, queued);
        AddCompletionMetrics(metrics, completed);
        AddActionMetrics(metrics, purged, "purge_");
        AddReadModelMetrics(metrics, queueCatchup, "post_queue");
        AddReadModelMetrics(metrics, completionCatchup, "post_completion");
        AddReadModelMetrics(metrics, purgeCatchup, "post_purge");
        AddMemoryMetrics(metrics, "memory_before", before);
        AddMemoryMetrics(metrics, "memory_after_completion", afterCompletion);
        AddMemoryMetrics(metrics, "memory_after_purge", afterPurge);
        metrics.Add("managed_memory_growth_before_purge_bytes", afterCompletion.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_retained_after_purge_bytes", afterPurge.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_released_by_purge_bytes", afterCompletion.ManagedBytes - afterPurge.ManagedBytes, "bytes");
        metrics.Add("private_memory_growth_before_purge_bytes", afterCompletion.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_retained_after_purge_bytes", afterPurge.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_released_by_purge_bytes", afterCompletion.PrivateBytes - afterPurge.PrivateBytes, "bytes");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunDurableMemoryReleaseAfterPurge(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var metrics = new ScenarioMetrics(scenario);
        var durability = await ResolveDurability(options);
        var before = CaptureMemory();

        MemorySnapshot afterCompletion;
        MemorySnapshot afterPurge;
        MemorySnapshot afterStop;
        QueueOperationResult queued;
        CompletionOperationResult completed;
        ActionOperationResult purged;
        ReadModelCatchupResult queueCatchup;
        ReadModelCatchupResult completionCatchup;
        ReadModelCatchupResult purgeCatchup;
        DurableStateCounts countsAfterCompletion;
        DurableStateCounts countsAfterPurge;

        await using (var harness = await DurableWorkBenchmarkSystem.Create(
            durability.ConnectionString,
            options.DurabilitySchemaName,
            resetStore: true,
            cancellationToken))
        {
            queued = await QueueNamedWorkers(
                harness.System,
                harness.DurableFastWorkName,
                options.Workers,
                options,
                scenario,
                startIndex: 0,
                cancellationToken);
            queueCatchup = await WaitForReadModel(harness.System, cancellationToken);
            completed = await WaitForCompletions(
                queued.Handles,
                options,
                cancellationToken);
            completionCatchup = await WaitForReadModel(harness.System, cancellationToken);
            await WaitForDurabilityIdle(harness.System, cancellationToken);
            countsAfterCompletion = await ReadDurableStateCounts(
                harness.ConnectionString,
                harness.DurabilitySchemaName,
                cancellationToken);
            afterCompletion = CaptureMemory();

            purged = await ExecuteWorkerAction(
                harness.System,
                queued.Handles,
                WorkAction.Purge,
                options,
                cancellationToken);
            purgeCatchup = await WaitForReadModel(harness.System, cancellationToken);
            await WaitForDurabilityIdle(harness.System, cancellationToken);
            await WaitForDurableState(
                harness.ConnectionString,
                harness.DurabilitySchemaName,
                static counts => counts.WorkEntries == 0 && counts.WorkflowRuns == 0,
                cancellationToken);
            countsAfterPurge = await ReadDurableStateCounts(
                harness.ConnectionString,
                harness.DurabilitySchemaName,
                cancellationToken);
            afterPurge = CaptureMemory();
        }

        afterStop = CaptureMemory();

        MemorySnapshot afterRestart;
        DurableStateCounts countsAfterRestart;
        WorkSystemDurabilityDiagnostics restartDurability;
        await using (var restarted = await DurableWorkBenchmarkSystem.Create(
            durability.ConnectionString,
            options.DurabilitySchemaName,
            resetStore: false,
            cancellationToken))
        {
            restartDurability = await WaitForDurabilityIdle(restarted.System, cancellationToken);
            countsAfterRestart = await ReadDurableStateCounts(
                restarted.ConnectionString,
                restarted.DurabilitySchemaName,
                cancellationToken);
            afterRestart = CaptureMemory();
        }

        AddQueueMetrics(metrics, queued);
        AddCompletionMetrics(metrics, completed);
        AddActionMetrics(metrics, purged, "purge_");
        AddReadModelMetrics(metrics, queueCatchup, "post_queue");
        AddReadModelMetrics(metrics, completionCatchup, "post_completion");
        AddReadModelMetrics(metrics, purgeCatchup, "post_purge");
        AddMemoryMetrics(metrics, "memory_before", before);
        AddMemoryMetrics(metrics, "memory_after_completion", afterCompletion);
        AddMemoryMetrics(metrics, "memory_after_purge", afterPurge);
        AddMemoryMetrics(metrics, "memory_after_stop", afterStop);
        AddMemoryMetrics(metrics, "memory_after_restart", afterRestart);
        AddDurabilityMetrics(metrics, "durability_after_restart", restartDurability);
        AddDurableStateMetrics(metrics, "durable_state_after_completion", countsAfterCompletion);
        AddDurableStateMetrics(metrics, "durable_state_after_purge", countsAfterPurge);
        AddDurableStateMetrics(metrics, "durable_state_after_restart", countsAfterRestart);
        metrics.Add("managed_memory_growth_before_purge_bytes", afterCompletion.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_retained_after_purge_bytes", afterPurge.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_released_by_purge_bytes", afterCompletion.ManagedBytes - afterPurge.ManagedBytes, "bytes");
        metrics.Add("managed_memory_retained_after_restart_bytes", afterRestart.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("private_memory_growth_before_purge_bytes", afterCompletion.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_retained_after_purge_bytes", afterPurge.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_released_by_purge_bytes", afterCompletion.PrivateBytes - afterPurge.PrivateBytes, "bytes");
        metrics.Add("private_memory_retained_after_restart_bytes", afterRestart.PrivateBytes - before.PrivateBytes, "bytes");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunDurableWorkflowMemoryRecovery(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var metrics = new ScenarioMetrics(scenario);
        var durability = await ResolveDurability(options);
        var runCount = Math.Max(1, options.Workers);
        var branchCount = Math.Max(1, options.ViewSubscriptions);
        var requestContext = BenchmarkRequestContexts.CreateAnonymous("Run durable workflow memory recovery benchmark.");
        var before = CaptureMemory();
        var runIds = new List<Guid>(runCount);
        var startStopwatch = Stopwatch.StartNew();

        MemorySnapshot afterInterruptedLoad;
        MemorySnapshot afterFirstStop;
        DurableStateCounts countsBeforeRestart;
        await using (var first = await DurableWorkflowBenchmarkSystem.Create(
            branchCount,
            _ => async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return WorkExecutionResult.Success();
            },
            durability.ConnectionString,
            options.DurabilitySchemaName,
            resetStore: true,
            cancellationToken))
        {
            for (var index = 0; index < runCount; index++)
            {
                runIds.Add(WorkflowBenchmarkReflection.Start(
                    first.System,
                    "perf.workflow.durable.parallel",
                    requestContext,
                    cancellationToken));
            }

            await WaitForDurableState(
                first.ConnectionString,
                first.DurabilitySchemaName,
                counts => counts.WorkflowRuns >= runCount,
                cancellationToken);
            startStopwatch.Stop();
            countsBeforeRestart = await ReadDurableStateCounts(
                first.ConnectionString,
                first.DurabilitySchemaName,
                cancellationToken);
            afterInterruptedLoad = CaptureMemory();
        }

        afterFirstStop = CaptureMemory();

        var recoveryStopwatch = Stopwatch.StartNew();
        MemorySnapshot afterRecoveryCompletion;
        MemorySnapshot afterRecoveryStop;
        DurableStateCounts countsAfterRecovery;
        WorkSystemDurabilityDiagnostics recoveryDurability;
        await using (var recovered = await DurableWorkflowBenchmarkSystem.Create(
            branchCount,
            _ => static (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            durability.ConnectionString,
            options.DurabilitySchemaName,
            resetStore: false,
            cancellationToken))
        {
            foreach (var runId in runIds)
            {
                var status = await DurableWorkflowBenchmarkSystem.WaitForFinalStatus(
                    recovered.System,
                    runId,
                    cancellationToken);
                if (status != WorkflowRunStatus.Completed)
                {
                    throw new InvalidOperationException(
                        $"Expected durable workflow run '{runId:D}' to complete during recovery, but it settled as '{status}'.");
                }
            }

            await WaitForDurabilityIdle(recovered.System, cancellationToken);
            await WaitForDurableState(
                recovered.ConnectionString,
                recovered.DurabilitySchemaName,
                static counts => counts.WorkEntries == 0 && counts.WorkflowRuns == 0,
                cancellationToken);
            recoveryStopwatch.Stop();
            recoveryDurability = recovered.System.Diagnostics.Durability;
            countsAfterRecovery = await ReadDurableStateCounts(
                recovered.ConnectionString,
                recovered.DurabilitySchemaName,
                cancellationToken);
            afterRecoveryCompletion = CaptureMemory();
        }

        afterRecoveryStop = CaptureMemory();

        MemorySnapshot afterCleanRestart;
        DurableStateCounts countsAfterCleanRestart;
        WorkSystemDurabilityDiagnostics cleanRestartDurability;
        await using (var cleanRestart = await DurableWorkflowBenchmarkSystem.Create(
            branchCount,
            _ => static (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            durability.ConnectionString,
            options.DurabilitySchemaName,
            resetStore: false,
            cancellationToken))
        {
            cleanRestartDurability = await WaitForDurabilityIdle(cleanRestart.System, cancellationToken);
            countsAfterCleanRestart = await ReadDurableStateCounts(
                cleanRestart.ConnectionString,
                cleanRestart.DurabilitySchemaName,
                cancellationToken);
            afterCleanRestart = CaptureMemory();
        }

        metrics.Add("workflow_runs", runCount, "runs");
        metrics.Add("workflow_branch_count", branchCount, "branches");
        metrics.Add("startup_elapsed_ms", startStopwatch.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("startup_runs_per_sec", Rate(runCount, startStopwatch.Elapsed), "runs/sec");
        metrics.Add("recovery_elapsed_ms", recoveryStopwatch.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("recovery_runs_per_sec", Rate(runCount, recoveryStopwatch.Elapsed), "runs/sec");
        AddMemoryMetrics(metrics, "memory_before", before);
        AddMemoryMetrics(metrics, "memory_after_interrupted_load", afterInterruptedLoad);
        AddMemoryMetrics(metrics, "memory_after_first_stop", afterFirstStop);
        AddMemoryMetrics(metrics, "memory_after_recovery_completion", afterRecoveryCompletion);
        AddMemoryMetrics(metrics, "memory_after_recovery_stop", afterRecoveryStop);
        AddMemoryMetrics(metrics, "memory_after_clean_restart", afterCleanRestart);
        AddDurabilityMetrics(metrics, "durability_after_recovery", recoveryDurability);
        AddDurabilityMetrics(metrics, "durability_after_clean_restart", cleanRestartDurability);
        AddDurableStateMetrics(metrics, "durable_state_before_restart", countsBeforeRestart);
        AddDurableStateMetrics(metrics, "durable_state_after_recovery", countsAfterRecovery);
        AddDurableStateMetrics(metrics, "durable_state_after_clean_restart", countsAfterCleanRestart);
        metrics.Add("managed_memory_growth_during_interrupted_load_bytes", afterInterruptedLoad.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_retained_after_recovery_bytes", afterRecoveryCompletion.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_retained_after_clean_restart_bytes", afterCleanRestart.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("private_memory_growth_during_interrupted_load_bytes", afterInterruptedLoad.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_retained_after_recovery_bytes", afterRecoveryCompletion.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_retained_after_clean_restart_bytes", afterCleanRestart.PrivateBytes - before.PrivateBytes, "bytes");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunEventFanout(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var metrics = new ScenarioMetrics(scenario);
        var maxSubscriptions = Math.Max(1, options.ViewSubscriptions);
        var subscriptionCounts = new[] { 0, 1, 2, maxSubscriptions }
            .Where(count => count <= maxSubscriptions)
            .Distinct()
            .ToArray();

        LifecyclePassResult? baseline = null;
        foreach (var subscriptionCount in subscriptionCounts)
        {
            var profileName = $"unfiltered_{subscriptionCount}";
            var result = await RunLifecyclePass(
                $"{scenario}-{profileName}",
                options,
                subscriptionCount,
                EventFanoutFilterMode.Unfiltered,
                cancellationToken);
            baseline ??= result;
            AddLifecycleFanoutMetrics(metrics, profileName, result, baseline.CompletedPerSecond);
        }

        foreach (var filterMode in new[]
        {
            EventFanoutFilterMode.EventTypeCompleted,
            EventFanoutFilterMode.EventTypeNoMatch,
            EventFanoutFilterMode.IdentifierMatch,
            EventFanoutFilterMode.IdentifierNoMatch,
        })
        {
            var profileName = $"{ToMetricName(filterMode)}_{maxSubscriptions}";
            var result = await RunLifecyclePass(
                $"{scenario}-{profileName}",
                options,
                maxSubscriptions,
                filterMode,
                cancellationToken);
            AddLifecycleFanoutMetrics(metrics, profileName, result, baseline?.CompletedPerSecond ?? result.CompletedPerSecond);
        }

        if (baseline is not null)
        {
            metrics.Add("baseline_completed_per_sec", baseline.CompletedPerSecond, "workers/sec");
            metrics.Add("baseline_read_model_catchup_ms", baseline.ReadModelCatchup.Elapsed.TotalMilliseconds, "ms");
            metrics.Add("baseline_enqueued_updates", baseline.ReadModelCatchup.End.EnqueuedSequence, "updates");
        }

        return metrics;
    }

    private static async Task<ScenarioMetrics> RunEventDelivery(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var metrics = new ScenarioMetrics(scenario);
        var maxSubscriptions = Math.Max(1, options.ViewSubscriptions);
        var profiles = new (string Name, int Count, EventFanoutFilterMode FilterMode)[]
        {
            ("unfiltered_1", 1, EventFanoutFilterMode.Unfiltered),
            ($"unfiltered_{maxSubscriptions}", maxSubscriptions, EventFanoutFilterMode.Unfiltered),
            ($"event_type_completed_{maxSubscriptions}", maxSubscriptions, EventFanoutFilterMode.EventTypeCompleted),
            ($"event_type_no_match_{maxSubscriptions}", maxSubscriptions, EventFanoutFilterMode.EventTypeNoMatch),
        };

        DeliveryPassResult? baseline = null;
        foreach (var profile in profiles)
        {
            var result = await RunDeliveryPass(
                $"{scenario}-{profile.Name}",
                options,
                profile.Count,
                profile.FilterMode,
                cancellationToken);
            baseline ??= result;
            AddDeliveryMetrics(
                metrics,
                profile.Name,
                result,
                baseline.CompletedPerSecond,
                baseline.DeliveredEventsPerSecond);
        }

        if (baseline is not null)
        {
            metrics.Add("baseline_completed_per_sec", baseline.CompletedPerSecond, "workers/sec");
            metrics.Add("baseline_delivered_events_per_sec", baseline.DeliveredEventsPerSecond, "events/sec");
        }

        return metrics;
    }

    private static async Task<ScenarioMetrics> RunSubscriptionChurn(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var cycles = Math.Max(1, options.ViewIterations);
        var subscriptionsPerCycle = Math.Max(1, options.ViewSubscriptions);
        var subscribeLatencies = new DurationRecorder();
        var unsubscribeLatencies = new DurationRecorder();
        var subscribeOperations = 0L;
        var unsubscribeOperations = 0L;
        var stopwatch = Stopwatch.StartNew();

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var subscriptions = new List<IWorkEventSubscription>(subscriptionsPerCycle);
            try
            {
                for (var index = 0; index < subscriptionsPerCycle; index++)
                {
                    var subscribeStopwatch = Stopwatch.StartNew();
                    subscriptions.Add(harness.System.Events.Subscribe(CreateSubscriptionChurnFilter(cycle, index)));
                    subscribeStopwatch.Stop();
                    subscribeLatencies.Record(subscribeStopwatch.Elapsed);
                    subscribeOperations++;
                }
            }
            finally
            {
                foreach (var subscription in subscriptions)
                {
                    var unsubscribeStopwatch = Stopwatch.StartNew();
                    await subscription.DisposeAsync();
                    unsubscribeStopwatch.Stop();
                    unsubscribeLatencies.Record(unsubscribeStopwatch.Elapsed);
                    unsubscribeOperations++;
                }
            }
        }

        stopwatch.Stop();
        metrics.Add("churn_cycles", cycles, "cycles");
        metrics.Add("subscriptions_per_cycle", subscriptionsPerCycle, "subscriptions");
        metrics.Add("subscribe_operations", subscribeOperations, "subscriptions");
        metrics.Add("unsubscribe_operations", unsubscribeOperations, "subscriptions");
        metrics.Add("churn_elapsed_ms", stopwatch.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("subscribe_ops_per_sec", Rate(subscribeOperations, stopwatch.Elapsed), "subscriptions/sec");
        metrics.Add("unsubscribe_ops_per_sec", Rate(unsubscribeOperations, stopwatch.Elapsed), "subscriptions/sec");
        metrics.Add("churn_ops_per_sec", Rate(subscribeOperations + unsubscribeOperations, stopwatch.Elapsed), "operations/sec");
        AddDurationMetrics(metrics, "subscribe_latency", subscribeLatencies.Snapshot());
        AddDurationMetrics(metrics, "unsubscribe_latency", unsubscribeLatencies.Snapshot());
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunSubscriptionMemoryRelease(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        var cycles = Math.Max(1, options.ViewIterations);
        var subscriptionsPerCycle = Math.Max(1, options.ViewSubscriptions);
        var totalSubscriptions = cycles * subscriptionsPerCycle;
        var subscribeLatencies = new DurationRecorder();
        var unsubscribeLatencies = new DurationRecorder();
        var subscriptions = new List<IWorkEventSubscription>(totalSubscriptions);
        var before = CaptureMemory();

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            for (var index = 0; index < subscriptionsPerCycle; index++)
            {
                var subscribeStopwatch = Stopwatch.StartNew();
                subscriptions.Add(harness.System.Events.Subscribe(CreateSubscriptionChurnFilter(cycle, index)));
                subscribeStopwatch.Stop();
                subscribeLatencies.Record(subscribeStopwatch.Elapsed);
            }
        }

        var afterSubscribe = CaptureMemory();
        foreach (var subscription in subscriptions)
        {
            var unsubscribeStopwatch = Stopwatch.StartNew();
            await subscription.DisposeAsync();
            unsubscribeStopwatch.Stop();
            unsubscribeLatencies.Record(unsubscribeStopwatch.Elapsed);
        }

        var afterDispose = CaptureMemory();

        metrics.Add("churn_cycles", cycles, "cycles");
        metrics.Add("subscriptions_per_cycle", subscriptionsPerCycle, "subscriptions");
        metrics.Add("total_subscriptions", totalSubscriptions, "subscriptions");
        AddDurationMetrics(metrics, "subscribe_latency", subscribeLatencies.Snapshot());
        AddDurationMetrics(metrics, "unsubscribe_latency", unsubscribeLatencies.Snapshot());
        AddMemoryMetrics(metrics, "memory_before", before);
        AddMemoryMetrics(metrics, "memory_after_subscribe", afterSubscribe);
        AddMemoryMetrics(metrics, "memory_after_dispose", afterDispose);
        metrics.Add("managed_memory_growth_during_subscribe_bytes", afterSubscribe.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_retained_after_dispose_bytes", afterDispose.ManagedBytes - before.ManagedBytes, "bytes");
        metrics.Add("managed_memory_released_after_dispose_bytes", afterSubscribe.ManagedBytes - afterDispose.ManagedBytes, "bytes");
        metrics.Add("private_memory_growth_during_subscribe_bytes", afterSubscribe.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_retained_after_dispose_bytes", afterDispose.PrivateBytes - before.PrivateBytes, "bytes");
        metrics.Add("private_memory_released_after_dispose_bytes", afterSubscribe.PrivateBytes - afterDispose.PrivateBytes, "bytes");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunPublishUnderChurn(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var metrics = new ScenarioMetrics(scenario);
        using var churnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var churnTask = RunSubscriptionChurnLoop(
            harness.System,
            Math.Max(1, options.ViewIterations),
            Math.Max(1, options.ViewSubscriptions),
            churnCancellation.Token);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            workerOptions: null,
            options,
            scenario,
            startIndex: 0,
            cancellationToken);
        var completionLatency = new DurationRecorder();
        var completed = 0;
        await RunParallel(
            queued.Handles.Count,
            options.Parallelism,
            async index =>
            {
                var waitStopwatch = Stopwatch.StartNew();
                var completion = await queued.Handles[index].WaitForCompletion(cancellationToken);
                waitStopwatch.Stop();
                completionLatency.Record(waitStopwatch.Elapsed);
                if (completion.IsCompletedSuccessfully)
                {
                    Interlocked.Increment(ref completed);
                }
            },
            cancellationToken);
        stopwatch.Stop();
        churnCancellation.Cancel();
        var churn = await churnTask;
        var catchup = await WaitForReadModel(harness.System, cancellationToken);
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;

        metrics.Add("accepted_workers", queued.AcceptedWorkers, "workers");
        metrics.Add("completed_workers", completed, "workers");
        metrics.Add("elapsed_ms", stopwatch.Elapsed.TotalMilliseconds, "ms");
        metrics.Add("completed_per_sec", Rate(completed, stopwatch.Elapsed), "workers/sec");
        metrics.Add("allocated_bytes", allocatedBytes, "bytes");
        metrics.Add("allocated_bytes_per_worker", PerWorker(allocatedBytes, completed), "bytes/worker");
        metrics.Add("churn_cycles", churn.Cycles, "cycles");
        metrics.Add("churn_subscribe_operations", churn.SubscribeOperations, "subscriptions");
        metrics.Add("churn_unsubscribe_operations", churn.UnsubscribeOperations, "subscriptions");
        metrics.Add("churn_total_ops_per_sec", Rate(churn.SubscribeOperations + churn.UnsubscribeOperations, stopwatch.Elapsed), "operations/sec");
        AddDurationMetrics(metrics, "queue_latency", queued.Latency);
        AddDurationMetrics(metrics, "completion_wait_latency", completionLatency.Snapshot());
        AddDurationMetrics(metrics, "churn_subscribe_latency", churn.SubscribeLatency);
        AddDurationMetrics(metrics, "churn_unsubscribe_latency", churn.UnsubscribeLatency);
        AddReadModelMetrics(metrics, catchup, "post_publish_under_churn");
        return metrics;
    }

    private static async Task<ScenarioMetrics> RunSignalRFanoutMatrix(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var metrics = new ScenarioMetrics(scenario);
        var maxSubscriptions = Math.Max(1, options.ViewSubscriptions);
        var profiles = new (string Name, int Count, SignalRRealtimeFilterMode FilterMode)[]
        {
            ("unfiltered_1", 1, SignalRRealtimeFilterMode.Unfiltered),
            ($"unfiltered_{maxSubscriptions}", maxSubscriptions, SignalRRealtimeFilterMode.Unfiltered),
            ($"event_type_queued_{maxSubscriptions}", maxSubscriptions, SignalRRealtimeFilterMode.EventTypeQueued),
            ($"definition_name_{maxSubscriptions}", maxSubscriptions, SignalRRealtimeFilterMode.DefinitionName),
            ($"identifier_{maxSubscriptions}", maxSubscriptions, SignalRRealtimeFilterMode.Identifier),
        };

        SignalRRealtimePassResult? baseline = null;
        foreach (var profile in profiles)
        {
            var result = await RunSignalRRealtimePass(
                $"{scenario}-{profile.Name}",
                options,
                profile.Count,
                profile.FilterMode,
                cancellationToken);
            baseline ??= result;
            AddSignalRRealtimeMetrics(metrics, profile.Name, result, baseline.DeliveredEventsPerSecond);
        }

        if (baseline is not null)
        {
            metrics.Add("baseline_delivered_events_per_sec", baseline.DeliveredEventsPerSecond, "events/sec");
            metrics.Add(
                "baseline_workers_per_sec",
                baseline.Queue.AcceptedWorkers <= 0 ? 0 : Rate(baseline.Queue.AcceptedWorkers, baseline.TotalElapsed),
                "workers/sec");
        }

        return metrics;
    }

    private static async Task<LifecyclePassResult> RunLifecyclePass(
        string scenario,
        HarnessOptions options,
        int subscriptionCount,
        EventFanoutFilterMode filterMode,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var subscriptions = new List<IWorkEventSubscription>(subscriptionCount);
        try
        {
            for (var index = 0; index < subscriptionCount; index++)
            {
                subscriptions.Add(harness.System.Events.Subscribe(CreateEventFanoutFilter(filterMode, scenario)));
            }

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var stopwatch = Stopwatch.StartNew();
            var queued = await QueueWorkers(
                harness.System,
                options.Workers,
                workerOptions: null,
                options,
                scenario,
                startIndex: 0,
                cancellationToken);
            var completionLatency = new DurationRecorder();
            var completed = 0;
            await RunParallel(
                queued.Handles.Count,
                options.Parallelism,
                async index =>
                {
                    var waitStopwatch = Stopwatch.StartNew();
                    var completion = await queued.Handles[index].WaitForCompletion(cancellationToken);
                    waitStopwatch.Stop();
                    completionLatency.Record(waitStopwatch.Elapsed);
                    if (completion.IsCompletedSuccessfully)
                    {
                        Interlocked.Increment(ref completed);
                    }
                },
                cancellationToken);
            stopwatch.Stop();
            var catchup = await WaitForReadModel(harness.System, cancellationToken);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            return new LifecyclePassResult(
                queued,
                completed,
                stopwatch.Elapsed,
                completionLatency.Snapshot(),
                catchup,
                subscriptionCount,
                filterMode,
                allocatedBytes,
                SummarizeSubscriptionDiagnostics(subscriptions));
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                await subscription.DisposeAsync();
            }
        }
    }

    private static async Task<DeliveryPassResult> RunDeliveryPass(
        string scenario,
        HarnessOptions options,
        int subscriptionCount,
        EventFanoutFilterMode filterMode,
        CancellationToken cancellationToken)
    {
        await using var harness = await HarnessSystem.Create(options, cancellationToken);
        var subscriptionOptions = new WorkEventSubscriptionOptions(
            Capacity: Math.Max(4096, options.Workers * 8),
            OverflowBehavior: WorkEventOverflowBehavior.DropOldest);
        var subscriptions = new List<IWorkEventSubscription>(subscriptionCount);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var consumerObservedEvents = new long[subscriptionCount];
        var consumerTasks = new Task[subscriptionCount];

        try
        {
            for (var index = 0; index < subscriptionCount; index++)
            {
                var subscription = harness.System.Events.Subscribe(
                    CreateEventFanoutFilter(filterMode, scenario),
                    subscriptionOptions);
                subscriptions.Add(subscription);
                consumerTasks[index] = ConsumeSubscription(
                    subscription,
                    index,
                    consumerObservedEvents,
                    readerCancellation.Token);
            }

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var stopwatch = Stopwatch.StartNew();
            var queued = await QueueWorkers(
                harness.System,
                options.Workers,
                workerOptions: null,
                options,
                scenario,
                startIndex: 0,
                cancellationToken);
            var completionLatency = new DurationRecorder();
            var completed = 0;
            await RunParallel(
                queued.Handles.Count,
                options.Parallelism,
                async index =>
                {
                    var waitStopwatch = Stopwatch.StartNew();
                    var completion = await queued.Handles[index].WaitForCompletion(cancellationToken);
                    waitStopwatch.Stop();
                    completionLatency.Record(waitStopwatch.Elapsed);
                    if (completion.IsCompletedSuccessfully)
                    {
                        Interlocked.Increment(ref completed);
                    }
                },
                cancellationToken);
            var catchup = await WaitForReadModel(harness.System, cancellationToken);
            await WaitForSubscriptionDrain(subscriptions, cancellationToken);
            stopwatch.Stop();
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            var diagnostics = SummarizeSubscriptionDiagnostics(subscriptions);
            var observedEvents = consumerObservedEvents.Sum();
            return new DeliveryPassResult(
                queued,
                completed,
                stopwatch.Elapsed,
                completionLatency.Snapshot(),
                catchup,
                subscriptionCount,
                filterMode,
                allocatedBytes,
                diagnostics,
                observedEvents);
        }
        finally
        {
            readerCancellation.Cancel();
            foreach (var subscription in subscriptions)
            {
                await subscription.DisposeAsync();
            }

            try
            {
                await Task.WhenAll(consumerTasks.Where(task => task is not null));
            }
            catch (OperationCanceledException) when (readerCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<SignalRRealtimePassResult> RunSignalRRealtimePass(
        string scenario,
        HarnessOptions options,
        int subscriptionCount,
        SignalRRealtimeFilterMode filterMode,
        CancellationToken cancellationToken)
    {
        const string eventType = "worker.queued";
        var timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(60, options.Workers / 25)));

        await using var host = await SignalRScenarioHost.Create(cancellationToken);
        var connections = new List<Microsoft.AspNetCore.SignalR.Client.HubConnection>(subscriptionCount);
        var counters = new List<SignalRRealtimeCounter>(subscriptionCount);
        var connectLatencies = new DurationRecorder();
        var watchLatencies = new DurationRecorder();
        var warmupBatch = $"{scenario}-warmup";
        var measuredBatch = $"{scenario}-measure";

        try
        {
            for (var index = 0; index < subscriptionCount; index++)
            {
                var counter = new SignalRRealtimeCounter(eventType);
                var connection = host.CreateSignalRConnection();
                CaptureSignalRRealtimeEvents(connection, counter.Observe);

                var connectStopwatch = Stopwatch.StartNew();
                await connection.StartAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
                connectStopwatch.Stop();
                connectLatencies.Record(connectStopwatch.Elapsed);

                var watchStopwatch = Stopwatch.StartNew();
                await connection.InvokeAsync(
                        "WatchEvents",
                        CreateSignalRRealtimeCriteria(filterMode, warmupBatch),
                        null,
                        cancellationToken)
                    .WaitAsync(timeout, cancellationToken);
                watchStopwatch.Stop();
                watchLatencies.Record(watchStopwatch.Elapsed);

                counter.Reset(expectedEvents: 1);
                connections.Add(connection);
                counters.Add(counter);
            }

            var warmupQueued = await QueueSignalRBenchmarkWorkers(host, 1, warmupBatch, options, cancellationToken);
            if (warmupQueued.AcceptedWorkers != 1 || warmupQueued.RejectedWorkers != 0)
            {
                throw new InvalidOperationException("SignalR realtime warmup queueing did not accept exactly one worker.");
            }

            await WaitForSignalRRealtimeCounters(counters, timeout, cancellationToken);

            foreach (var counter in counters)
            {
                counter.Reset(options.Workers);
            }

            for (var index = 0; index < connections.Count; index++)
            {
                if (filterMode == SignalRRealtimeFilterMode.Identifier)
                {
                    var rewatchStopwatch = Stopwatch.StartNew();
                    await connections[index].InvokeAsync(
                            "UnwatchEvents",
                            CreateSignalRRealtimeCriteria(filterMode, warmupBatch),
                            null,
                            cancellationToken)
                        .WaitAsync(timeout, cancellationToken);
                    await connections[index].InvokeAsync(
                            "WatchEvents",
                            CreateSignalRRealtimeCriteria(filterMode, measuredBatch),
                            null,
                            cancellationToken)
                        .WaitAsync(timeout, cancellationToken);
                    rewatchStopwatch.Stop();
                    watchLatencies.Record(rewatchStopwatch.Elapsed);
                }
            }

            var totalStopwatch = Stopwatch.StartNew();
            var queued = await QueueSignalRBenchmarkWorkers(host, options.Workers, measuredBatch, options, cancellationToken);
            if (queued.RejectedWorkers != 0)
            {
                throw new InvalidOperationException(
                    $"SignalR realtime pass '{scenario}' rejected {queued.RejectedWorkers} workers unexpectedly.");
            }

            var deliveryStopwatch = Stopwatch.StartNew();
            await WaitForSignalRRealtimeCounters(counters, timeout, cancellationToken);
            deliveryStopwatch.Stop();
            totalStopwatch.Stop();

            var observedEvents = counters.Sum(counter => counter.ObservedCount);
            return new SignalRRealtimePassResult(
                queued,
                subscriptionCount,
                filterMode,
                totalStopwatch.Elapsed,
                deliveryStopwatch.Elapsed,
                connectLatencies.Snapshot(),
                watchLatencies.Snapshot(),
                observedEvents);
        }
        finally
        {
            for (var index = connections.Count - 1; index >= 0; index--)
            {
                await connections[index].DisposeAsync();
            }
        }
    }

    private static WorkEventFilter? CreateEventFanoutFilter(EventFanoutFilterMode filterMode, string scenario)
        => filterMode switch
        {
            EventFanoutFilterMode.Unfiltered => null,
            EventFanoutFilterMode.EventTypeCompleted => new WorkEventFilter(EventType: "worker.completed"),
            EventFanoutFilterMode.EventTypeNoMatch => new WorkEventFilter(EventType: "worker.__no_match"),
            EventFanoutFilterMode.IdentifierMatch => new WorkEventFilter(Identifier: new WorkIdentifier("batch", scenario)),
            EventFanoutFilterMode.IdentifierNoMatch => new WorkEventFilter(Identifier: new WorkIdentifier("batch", "event-fanout-no-match")),
            _ => throw new ArgumentOutOfRangeException(nameof(filterMode), filterMode, "Unknown fanout filter mode."),
        };

    private static WorkableRealtimeEventCriteria CreateSignalRRealtimeCriteria(
        SignalRRealtimeFilterMode filterMode,
        string batchValue)
        => filterMode switch
        {
            SignalRRealtimeFilterMode.Unfiltered => new WorkableRealtimeEventCriteria(),
            SignalRRealtimeFilterMode.EventTypeQueued => new WorkableRealtimeEventCriteria(EventTypes: ["worker.queued"]),
            SignalRRealtimeFilterMode.DefinitionName => new WorkableRealtimeEventCriteria(
                EventTypes: ["worker.queued"],
                DefinitionNames: ["perf.transport.queued"]),
            SignalRRealtimeFilterMode.Identifier => new WorkableRealtimeEventCriteria(
                EventTypes: ["worker.queued"],
                Keys:
                [
                    new WorkableRealtimeEventKeyCriteria(
                        WorkKeyKind.Identifier,
                        "batch",
                        batchValue),
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(filterMode), filterMode, "Unknown SignalR realtime filter mode."),
        };

    private static void CaptureSignalRRealtimeEvents(
        Microsoft.AspNetCore.SignalR.Client.HubConnection connection,
        Action<WorkableRealtimeEvent> onEvent)
    {
        connection.On<WorkableRealtimeEvent>(
            WorkableRealtimeClientMethods.WorkEvent,
            onEvent);
        connection.On<WorkableRealtimeEventBatch>(
            WorkableRealtimeClientMethods.WorkEvents,
            batch =>
            {
                foreach (var workEvent in batch.Events)
                {
                    onEvent(workEvent);
                }
            });
    }

    private static async Task<QueueOperationResult> QueueSignalRBenchmarkWorkers(
        SignalRScenarioHost host,
        int workerCount,
        string batchValue,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        var handles = new IWorkerHandle[workerCount];
        var latencies = new DurationRecorder();
        var acceptedWorkers = 0;
        var rejectedWorkers = 0;
        var requestContext = host.CreateRequestContext("Queue SignalR realtime benchmark workers.");
        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            workerCount,
            options.Parallelism,
            async index =>
            {
                var queueStopwatch = Stopwatch.StartNew();
                var handle = await host.System.CreateSession(requestContext).Queue.Enqueue(
                    "perf.transport.queued",
                    WorkableBenchmarkSystem.CreateInput(index)
                        .WithIdentifier(new WorkIdentifier("batch", batchValue)),
                    cancellationToken: cancellationToken);
                queueStopwatch.Stop();
                latencies.Record(queueStopwatch.Elapsed);
                handles[index] = handle;
                if (handle.QueueOutcome.IsAccepted)
                {
                    Interlocked.Increment(ref acceptedWorkers);
                }
                else
                {
                    Interlocked.Increment(ref rejectedWorkers);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        return new QueueOperationResult(
            [.. handles.Where(handle => handle?.QueueOutcome.IsAccepted == true)],
            acceptedWorkers,
            rejectedWorkers,
            stopwatch.Elapsed,
            latencies.Snapshot());
    }

    private static async Task WaitForSignalRRealtimeCounters(
        IReadOnlyList<SignalRRealtimeCounter> counters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(counters.Select(counter => counter.WaitAsync(timeout, cancellationToken)));
        }
        catch (TimeoutException ex)
        {
            var progress = string.Join(
                ", ",
                counters.Select((counter, index) => $"connection-{index}:{counter.ObservedCount}/{counter.ExpectedCount}"));
            throw new TimeoutException($"SignalR realtime delivery timed out waiting for subscribers to observe the expected event counts. Progress: {progress}", ex);
        }
    }

    private static async Task ConsumeSubscription(
        IWorkEventSubscription subscription,
        int index,
        long[] observedEvents,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in subscription.Read(cancellationToken))
            {
                Interlocked.Increment(ref observedEvents[index]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static WorkEventFilter? CreateSubscriptionChurnFilter(int cycle, int index)
        => ((cycle + index) % 5) switch
        {
            0 => null,
            1 => new WorkEventFilter(EventType: "worker.completed"),
            2 => new WorkEventFilter(EventType: "worker.queued", DefinitionName: WorkName(index)),
            3 => new WorkEventFilter(Identifier: new WorkIdentifier("segment", $"segment-{index % 32:D2}")),
            4 => new WorkEventFilter(Identifier: new WorkIdentifier("batch", $"subscription-churn-{cycle % 8:D2}")),
            _ => null,
        };

    private static async Task<SubscriptionChurnLoopResult> RunSubscriptionChurnLoop(
        IWorkSystem system,
        int maxCycles,
        int subscriptionsPerCycle,
        CancellationToken cancellationToken)
    {
        var subscribeLatencies = new DurationRecorder();
        var unsubscribeLatencies = new DurationRecorder();
        var subscribeOperations = 0L;
        var unsubscribeOperations = 0L;
        var cycles = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && cycles < maxCycles)
            {
                var subscriptions = new List<IWorkEventSubscription>(subscriptionsPerCycle);
                try
                {
                    for (var index = 0; index < subscriptionsPerCycle; index++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var subscribeStopwatch = Stopwatch.StartNew();
                        subscriptions.Add(system.Events.Subscribe(CreateSubscriptionChurnFilter(cycles, index)));
                        subscribeStopwatch.Stop();
                        subscribeLatencies.Record(subscribeStopwatch.Elapsed);
                        subscribeOperations++;
                    }
                }
                finally
                {
                    foreach (var subscription in subscriptions)
                    {
                        var unsubscribeStopwatch = Stopwatch.StartNew();
                        await subscription.DisposeAsync();
                        unsubscribeStopwatch.Stop();
                        unsubscribeLatencies.Record(unsubscribeStopwatch.Elapsed);
                        unsubscribeOperations++;
                    }
                }

                cycles++;
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        return new SubscriptionChurnLoopResult(
            cycles,
            subscribeOperations,
            unsubscribeOperations,
            subscribeLatencies.Snapshot(),
            unsubscribeLatencies.Snapshot());
    }

    private static async Task WaitForSubscriptionDrain(
        IReadOnlyList<IWorkEventSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        if (subscriptions.Count == 0)
        {
            return;
        }

        var spins = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = false;
            foreach (var subscription in subscriptions)
            {
                if (subscription is not IWorkEventSubscriptionDiagnostics diagnostics)
                {
                    continue;
                }

                var snapshot = diagnostics.GetDiagnosticsSnapshot();
                if (snapshot.QueuedCount > 0 || snapshot.DeliveredEventCount + snapshot.DroppedEventCount < snapshot.AcceptedEventCount)
                {
                    pending = true;
                    break;
                }
            }

            if (!pending)
            {
                return;
            }

            if (spins++ < 10)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private static EventSubscriptionDiagnosticsSummary SummarizeSubscriptionDiagnostics(
        IReadOnlyList<IWorkEventSubscription> subscriptions)
    {
        var queued = 0;
        var peakQueued = 0;
        var accepted = 0L;
        var delivered = 0L;
        var dropped = 0L;
        foreach (var subscription in subscriptions)
        {
            if (subscription is not IWorkEventSubscriptionDiagnostics diagnostics)
            {
                continue;
            }

            var snapshot = diagnostics.GetDiagnosticsSnapshot();
            queued += snapshot.QueuedCount;
            peakQueued += snapshot.PeakQueuedCount;
            accepted += snapshot.AcceptedEventCount;
            delivered += snapshot.DeliveredEventCount;
            dropped += snapshot.DroppedEventCount;
        }

        return new EventSubscriptionDiagnosticsSummary(
            subscriptions.Count,
            queued,
            peakQueued,
            accepted,
            delivered,
            dropped);
    }

    private static async Task<QueueOperationResult> QueueWorkers(
        IWorkSystem system,
        int count,
        WorkerOptions? workerOptions,
        HarnessOptions options,
        string scenario,
        int startIndex,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return QueueOperationResult.Empty;
        }

        var handles = new IWorkerHandle[count];
        var durations = new DurationRecorder();
        var accepted = 0;
        var rejected = 0;
        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            count,
            options.Parallelism,
            async index =>
            {
                var workerIndex = startIndex + index;
                var duration = Stopwatch.StartNew();
                var handle = await system.Queue.Enqueue(
                    WorkName(workerIndex),
                    CreateInput(scenario, workerIndex),
                    workerOptions,
                    cancellationToken);
                duration.Stop();
                durations.Record(duration.Elapsed);
                handles[index] = handle;
                if (handle.QueueOutcome.IsAccepted)
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    Interlocked.Increment(ref rejected);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        return new QueueOperationResult(
            [.. handles.Where(handle => handle.QueueOutcome.IsAccepted)],
            accepted,
            rejected,
            stopwatch.Elapsed,
            durations.Snapshot());
    }

    private static async Task<QueueOperationResult> QueueNamedWorkers(
        IWorkSystem system,
        string workName,
        int count,
        HarnessOptions options,
        string scenario,
        int startIndex,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return QueueOperationResult.Empty;
        }

        var handles = new IWorkerHandle[count];
        var durations = new DurationRecorder();
        var accepted = 0;
        var rejected = 0;
        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            count,
            options.Parallelism,
            async index =>
            {
                var workerIndex = startIndex + index;
                var duration = Stopwatch.StartNew();
                var handle = await system.Queue.Enqueue(
                    workName,
                    CreateInput(scenario, workerIndex),
                    options: null,
                    cancellationToken);
                duration.Stop();
                durations.Record(duration.Elapsed);
                handles[index] = handle;
                if (handle.QueueOutcome.IsAccepted)
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    Interlocked.Increment(ref rejected);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        return new QueueOperationResult(
            [.. handles.Where(handle => handle.QueueOutcome.IsAccepted)],
            accepted,
            rejected,
            stopwatch.Elapsed,
            durations.Snapshot());
    }

    private static async Task<StartToCompletionOperationResult> QueueAndWaitStartToCompletion(
        IWorkSystem system,
        int count,
        HarnessOptions options,
        string scenario,
        LifecycleStageRecorder stages,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return StartToCompletionOperationResult.Empty;
        }

        var queueDurations = new DurationRecorder();
        var completionDurations = new DurationRecorder();
        var accepted = 0;
        var rejected = 0;
        var completed = 0;
        await RunParallel(
            count,
            options.Parallelism,
            async index =>
            {
                stages.MarkQueueStarted(index);
                var queueStopwatch = Stopwatch.StartNew();
                var handle = await system.Queue.Enqueue(
                    WorkName(index),
                    CreateInstrumentedInput(scenario, index),
                    options: null,
                    cancellationToken);
                queueStopwatch.Stop();
                stages.MarkQueueCompleted(index);
                queueDurations.Record(queueStopwatch.Elapsed);

                if (!handle.QueueOutcome.IsAccepted)
                {
                    Interlocked.Increment(ref rejected);
                    return;
                }

                Interlocked.Increment(ref accepted);
                var completionStopwatch = Stopwatch.StartNew();
                var completion = await handle.WaitForCompletion(cancellationToken);
                completionStopwatch.Stop();
                stages.MarkCompletionObserved(index);
                completionDurations.Record(completionStopwatch.Elapsed);
                if (completion.IsCompletedSuccessfully)
                {
                    Interlocked.Increment(ref completed);
                }
            },
            cancellationToken);

        return new StartToCompletionOperationResult(
            accepted,
            rejected,
            completed,
            queueDurations.Snapshot(),
            completionDurations.Snapshot());
    }

    private static async Task<CompletionOperationResult> CompleteWorkers(
        IWorkSystem system,
        IReadOnlyList<IWorkerHandle> handles,
        IReadOnlyList<WorkerVersion> versions,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        if (handles.Count == 0)
        {
            return CompletionOperationResult.Empty;
        }

        var startDurations = new DurationRecorder();
        var completionDurations = new DurationRecorder();
        var acceptedStarts = 0;
        var completed = 0;
        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            handles.Count,
            options.Parallelism,
            async index =>
            {
                var startStopwatch = Stopwatch.StartNew();
                var outcome = await system.Workers.Execute(
                    versions[index],
                    WorkAction.Start,
                    cancellationToken);
                startStopwatch.Stop();
                startDurations.Record(startStopwatch.Elapsed);
                if (outcome.IsAccepted)
                {
                    Interlocked.Increment(ref acceptedStarts);
                }

                var completionStopwatch = Stopwatch.StartNew();
                var completion = await handles[index].WaitForCompletion(cancellationToken);
                completionStopwatch.Stop();
                completionDurations.Record(completionStopwatch.Elapsed);
                if (completion.IsCompletedSuccessfully)
                {
                    Interlocked.Increment(ref completed);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        return new CompletionOperationResult(
            handles.Count,
            acceptedStarts,
            completed,
            stopwatch.Elapsed,
            startDurations.Snapshot(),
            completionDurations.Snapshot());
    }

    private static async Task<CompletionOperationResult> WaitForCompletions(
        IReadOnlyList<IWorkerHandle> handles,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        if (handles.Count == 0)
        {
            return CompletionOperationResult.Empty;
        }

        var completionDurations = new DurationRecorder();
        var completed = 0;
        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            handles.Count,
            options.Parallelism,
            async index =>
            {
                var completionStopwatch = Stopwatch.StartNew();
                var completion = await handles[index].WaitForCompletion(cancellationToken);
                completionStopwatch.Stop();
                completionDurations.Record(completionStopwatch.Elapsed);
                if (completion.IsCompletedSuccessfully)
                {
                    Interlocked.Increment(ref completed);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        return new CompletionOperationResult(
            handles.Count,
            handles.Count,
            completed,
            stopwatch.Elapsed,
            new DurationSnapshot(0, 0, 0, 0, 0, 0),
            completionDurations.Snapshot());
    }

    private static async Task<ActionOperationResult> ExecuteWorkerAction(
        IWorkSystem system,
        IReadOnlyList<IWorkerHandle> handles,
        WorkAction action,
        HarnessOptions options,
        CancellationToken cancellationToken)
    {
        if (handles.Count == 0)
        {
            return ActionOperationResult.Empty with { Action = action };
        }

        var versions = await GetWorkerVersions(system, handles, cancellationToken);
        var actionDurations = new DurationRecorder();
        var accepted = 0;
        var rejected = 0;
        var stopwatch = Stopwatch.StartNew();
        await RunParallel(
            handles.Count,
            options.Parallelism,
            async index =>
            {
                var actionStopwatch = Stopwatch.StartNew();
                var outcome = await system.Workers.Execute(
                    versions[index],
                    action,
                    cancellationToken);
                actionStopwatch.Stop();
                actionDurations.Record(actionStopwatch.Elapsed);
                if (outcome.IsAccepted)
                {
                    Interlocked.Increment(ref accepted);
                }
                else
                {
                    Interlocked.Increment(ref rejected);
                }
            },
            cancellationToken);
        stopwatch.Stop();

        return new ActionOperationResult(
            action,
            handles.Count,
            accepted,
            rejected,
            stopwatch.Elapsed,
            actionDurations.Snapshot());
    }

    private static async Task<IReadOnlyList<WorkerVersion>> GetWorkerVersions(
        IWorkSystem system,
        IReadOnlyList<IWorkerHandle> handles,
        CancellationToken cancellationToken)
    {
        var versions = new WorkerVersion[handles.Count];
        await RunParallel(
            handles.Count,
            degreeOfParallelism: Math.Max(1, Environment.ProcessorCount),
            async index =>
            {
                var workerId = handles[index].WorkerId
                    ?? throw new InvalidOperationException("Accepted worker handle did not include a worker id.");
                var snapshot = await system.Query.Worker(workerId, cancellationToken)
                    ?? throw new InvalidOperationException($"Worker '{workerId}' was not found.");
                versions[index] = snapshot.Version;
            },
            cancellationToken);
        return versions;
    }

    private static async Task<ReadModelCatchupResult> WaitForReadModel(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var start = system.Diagnostics.ReadModel;
        var stopwatch = Stopwatch.StartNew();
        await WaitForAppliedSequence(system, start.EnqueuedSequence, cancellationToken);
        stopwatch.Stop();
        var end = system.Diagnostics.ReadModel;
        return new ReadModelCatchupResult(start, end, stopwatch.Elapsed);
    }

    private static async Task WaitForAppliedSequence(
        IWorkSystem system,
        long targetSequence,
        CancellationToken cancellationToken)
    {
        var spins = 0;
        while (system.Diagnostics.ReadModel.AppliedSequence < targetSequence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spins++ < 10)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private static async Task<WorkSystemDurabilityDiagnostics> WaitForDurabilityIdle(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = system.Diagnostics.Durability;
            if (diagnostics.HasCleanupFailure || diagnostics.HasLeaseRenewalFailure || diagnostics.HasReaderFailure)
            {
                throw new InvalidOperationException(
                    $"Durability diagnostics reported a failure. Reader='{diagnostics.ReaderFailureType}', LeaseRenewal='{diagnostics.LeaseRenewalFailureType}', Cleanup='{diagnostics.CleanupFailureType}'.");
            }

            if (diagnostics.AcceptedWaiterCount == 0 && diagnostics.PendingCleanupCount == 0)
            {
                return diagnostics;
            }

            await Task.Delay(10, cancellationToken);
        }

        var timeoutDiagnostics = system.Diagnostics.Durability;
        throw new TimeoutException(
            $"Timed out waiting for durable queue activity to settle. AcceptedWaiters={timeoutDiagnostics.AcceptedWaiterCount}, PendingCleanup={timeoutDiagnostics.PendingCleanupCount}.");
    }

    private static async Task<DurableStateCounts> ReadDurableStateCounts(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
SELECT
    CAST((SELECT COUNT_BIG(*) FROM {QuoteIdentifier(schemaName)}.[WorkEntries]) AS bigint) AS WorkEntries,
    CAST((SELECT COUNT_BIG(*) FROM {QuoteIdentifier(schemaName)}.[WorkflowRuns]) AS bigint) AS WorkflowRuns;
""";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new DurableStateCounts(
            reader.GetInt64(0),
            reader.GetInt64(1));
    }

    private static async Task<DurableStateCounts> WaitForDurableState(
        string connectionString,
        string schemaName,
        Func<DurableStateCounts, bool> predicate,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        DurableStateCounts latest = new(0, 0);
        while (Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await ReadDurableStateCounts(connectionString, schemaName, cancellationToken);
            if (predicate(latest))
            {
                return latest;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out waiting for durable state to settle. WorkEntries={latest.WorkEntries}, WorkflowRuns={latest.WorkflowRuns}.");
    }

    private static async Task RunParallel(
        int count,
        int degreeOfParallelism,
        Func<int, Task> action,
        CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        var next = -1;
        var workers = new Task[Math.Min(count, Math.Max(1, degreeOfParallelism))];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref next);
                    if (index >= count)
                    {
                        return;
                    }

                    await action(index);
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
    }

    private static MemorySnapshot CaptureMemory()
        => new(
            GC.GetTotalMemory(forceFullCollection: true),
            Process.GetCurrentProcess().PrivateMemorySize64);

    private static HarnessQueueMode EnsureInMemory(HarnessQueueMode mode)
        => mode == HarnessQueueMode.InMemory
            ? mode
            : throw new InvalidOperationException("Scenario benchmarks support only the in-memory queue mode.");

    private static void ValidateScenarioQueueModes(
        IReadOnlyList<string> scenarios,
        HarnessQueueMode queueMode)
    {
        foreach (var scenario in scenarios)
        {
            if (DurableOnlyScenarios.Contains(scenario, StringComparer.Ordinal))
            {
                if (!queueMode.IsDurable())
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenario}' requires a durable queue mode. Use '--queue-mode durable-idempotent' or '--queue-mode durable-non-idempotent'.");
                }

                continue;
            }

            if (queueMode != HarnessQueueMode.InMemory)
            {
                throw new InvalidOperationException(
                    $"Scenario '{scenario}' currently targets the in-memory backend. Use '--queue-mode in-memory', or run a durable-specific scenario.");
            }
        }
    }

    private static string[] ResolveScenarios(string scenario)
    {
        var normalized = NormalizeScenario(scenario);
        if (normalized == "all")
        {
            return AllScenarios;
        }

        if (normalized is "event-fanout-matrix" or "signalr-fanout-matrix" or "durable-memory-release-after-purge" or "durable-workflow-memory-recovery")
        {
            return [normalized];
        }

        return AllScenarios.Contains(normalized, StringComparer.Ordinal)
            ? [normalized]
            : throw new ArgumentException($"Unknown scenario '{scenario}'. Use --help for supported scenarios.");
    }

    private static string NormalizeScenario(string scenario)
        => scenario.Trim().ToLowerInvariant() switch
        {
            "mixed" => "mixed-queue-complete",
            "mixed-queueing-completion" => "mixed-queue-complete",
            "completion-heavy" => "queue-while-completion-heavy",
            "queue-heavy" => "completion-while-queue-heavy",
            var value => value,
        };

    private static int MixedQueueCount(int total, double queueFraction)
    {
        if (total <= 1)
        {
            return total;
        }

        return Math.Clamp((int)Math.Round(total * queueFraction), 1, total - 1);
    }

    private static WorkInput CreateInput(string scenario, int index)
        => CreateInput(scenario, index, CreateSubject(scenario, index));

    private static WorkInput CreateInput(string scenario, int index, WorkSubjectId subject)
    {
        var parity = index % 2 == 0 ? "even" : "odd";
        return WorkInput.Empty
            .WithSubject(subject)
            .WithIdentifier(new WorkIdentifier("batch", scenario))
            .WithIdentifier(new WorkIdentifier("parity", parity))
            .WithIdentifier(new WorkIdentifier("segment", $"segment-{index % 32:D2}"));
    }

    private static WorkInput CreateInstrumentedInput(string scenario, int index)
    {
        var parity = index % 2 == 0 ? "even" : "odd";
        return WorkInput.FromJson(
            index.ToString(CultureInfo.InvariantCulture),
            subjectId: CreateSubject(scenario, index),
            identifiers:
            [
                new WorkIdentifier("batch", scenario),
                new WorkIdentifier("parity", parity),
                new WorkIdentifier("segment", $"segment-{index % 32:D2}"),
            ]);
    }

    private static WorkSubjectId CreateSubject(string scenario, int index)
        => new("perf-worker", $"{scenario}-{index.ToString(CultureInfo.InvariantCulture)}");

    private static string WorkName(int index)
        => index % 2 == 0 ? EvenWorkName : OddWorkName;

    private static Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> CreateWorkExecutor(TimeSpan delay)
        => async (_, _, cancellationToken) =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return WorkExecutionResult.Success();
        };

    private static Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> CreateInstrumentedWorkExecutor(
        TimeSpan delay,
        LifecycleStageRecorder stages)
        => async (_, input, cancellationToken) =>
        {
            var index = ParseInstrumentedWorkerIndex(input);
            stages.MarkExecutorStarted(index);
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                return WorkExecutionResult.Success();
            }
            finally
            {
                stages.MarkExecutorCompleted(index);
            }
        };

    private static int ParseInstrumentedWorkerIndex(WorkInput? input)
    {
        if (input?.Json is not { } json ||
            !int.TryParse(json, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw new InvalidOperationException("Instrumented lifecycle benchmark input did not contain a worker index.");
        }

        return index;
    }

    private static void AddLifecycleFanoutMetrics(
        ScenarioMetrics metrics,
        string prefix,
        LifecyclePassResult result,
        double baselineCompletedPerSecond)
    {
        metrics.Add($"{prefix}_subscriptions", result.SubscriptionCount, "subscriptions");
        metrics.Add($"{prefix}_filter_mode", ToMetricName(result.FilterMode), "mode");
        metrics.Add($"{prefix}_completed_workers", result.CompletedWorkers, "workers");
        metrics.Add($"{prefix}_elapsed_ms", result.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_completed_per_sec", result.CompletedPerSecond, "workers/sec");
        metrics.Add(
            $"{prefix}_completed_per_sec_ratio",
            Math.Abs(baselineCompletedPerSecond) <= double.Epsilon
                ? 0
                : result.CompletedPerSecond / baselineCompletedPerSecond,
            "ratio");
        metrics.Add($"{prefix}_allocated_bytes", result.AllocatedBytes, "bytes");
        metrics.Add($"{prefix}_allocated_bytes_per_worker", PerWorker(result.AllocatedBytes, result.CompletedWorkers), "bytes/worker");
        metrics.Add($"{prefix}_subscription_queued_events", result.SubscriptionDiagnostics.QueuedEvents, "events");
        metrics.Add($"{prefix}_subscription_peak_queued_events", result.SubscriptionDiagnostics.PeakQueuedEvents, "events");
        metrics.Add($"{prefix}_subscription_accepted_events", result.SubscriptionDiagnostics.AcceptedEvents, "events");
        metrics.Add($"{prefix}_subscription_delivered_events", result.SubscriptionDiagnostics.DeliveredEvents, "events");
        metrics.Add($"{prefix}_subscription_dropped_events", result.SubscriptionDiagnostics.DroppedEvents, "events");
        metrics.Add($"{prefix}_read_model_catchup_ms", result.ReadModelCatchup.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_read_model_enqueued_updates", result.ReadModelCatchup.End.EnqueuedSequence, "updates");
        metrics.Add($"{prefix}_read_model_applied_delta", result.ReadModelCatchup.AppliedUpdateDelta, "updates");
        AddDurationMetrics(metrics, $"{prefix}_completion_wait_latency", result.CompletionLatency);
    }

    private static void AddDeliveryMetrics(
        ScenarioMetrics metrics,
        string prefix,
        DeliveryPassResult result,
        double baselineCompletedPerSecond,
        double baselineDeliveredEventsPerSecond)
    {
        metrics.Add($"{prefix}_subscriptions", result.SubscriptionCount, "subscriptions");
        metrics.Add($"{prefix}_filter_mode", ToMetricName(result.FilterMode), "mode");
        metrics.Add($"{prefix}_completed_workers", result.CompletedWorkers, "workers");
        metrics.Add($"{prefix}_elapsed_ms", result.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_completed_per_sec", result.CompletedPerSecond, "workers/sec");
        metrics.Add(
            $"{prefix}_completed_per_sec_ratio",
            Math.Abs(baselineCompletedPerSecond) <= double.Epsilon
                ? 0
                : result.CompletedPerSecond / baselineCompletedPerSecond,
            "ratio");
        metrics.Add($"{prefix}_delivered_events", result.DeliveredEvents, "events");
        metrics.Add($"{prefix}_observed_events", result.ObservedEvents, "events");
        metrics.Add($"{prefix}_dropped_events", result.DroppedEvents, "events");
        metrics.Add($"{prefix}_delivered_events_per_sec", result.DeliveredEventsPerSecond, "events/sec");
        metrics.Add(
            $"{prefix}_delivered_events_per_sec_ratio",
            Math.Abs(baselineDeliveredEventsPerSecond) <= double.Epsilon
                ? 0
                : result.DeliveredEventsPerSecond / baselineDeliveredEventsPerSecond,
            "ratio");
        metrics.Add($"{prefix}_allocated_bytes", result.AllocatedBytes, "bytes");
        metrics.Add($"{prefix}_allocated_bytes_per_worker", PerWorker(result.AllocatedBytes, result.CompletedWorkers), "bytes/worker");
        metrics.Add($"{prefix}_read_model_catchup_ms", result.ReadModelCatchup.Elapsed.TotalMilliseconds, "ms");
        AddDurationMetrics(metrics, $"{prefix}_completion_wait_latency", result.CompletionLatency);
    }

    private static void AddSignalRRealtimeMetrics(
        ScenarioMetrics metrics,
        string prefix,
        SignalRRealtimePassResult result,
        double baselineDeliveredEventsPerSecond)
    {
        metrics.Add($"{prefix}_subscriptions", result.SubscriptionCount, "subscriptions");
        metrics.Add($"{prefix}_filter_mode", ToMetricName(result.FilterMode), "mode");
        metrics.Add($"{prefix}_accepted_workers", result.Queue.AcceptedWorkers, "workers");
        metrics.Add($"{prefix}_rejected_workers", result.Queue.RejectedWorkers, "workers");
        metrics.Add($"{prefix}_observed_events", result.ObservedEvents, "events");
        metrics.Add($"{prefix}_expected_events", result.ExpectedEvents, "events");
        metrics.Add($"{prefix}_total_elapsed_ms", result.TotalElapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_delivery_wait_ms", result.DeliveryWaitElapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_workers_per_sec", Rate(result.Queue.AcceptedWorkers, result.TotalElapsed), "workers/sec");
        metrics.Add($"{prefix}_delivered_events_per_sec", result.DeliveredEventsPerSecond, "events/sec");
        metrics.Add(
            $"{prefix}_delivered_events_per_sec_ratio",
            Math.Abs(baselineDeliveredEventsPerSecond) <= double.Epsilon
                ? 0
                : result.DeliveredEventsPerSecond / baselineDeliveredEventsPerSecond,
            "ratio");
        AddDurationMetrics(metrics, $"{prefix}_queue_latency", result.Queue.Latency);
        AddDurationMetrics(metrics, $"{prefix}_signalr_connect_latency", result.ConnectLatency);
        AddDurationMetrics(metrics, $"{prefix}_signalr_watch_latency", result.WatchLatency);
    }

    private static string ToMetricName(EventFanoutFilterMode filterMode)
        => filterMode switch
        {
            EventFanoutFilterMode.Unfiltered => "unfiltered",
            EventFanoutFilterMode.EventTypeCompleted => "event_type_completed",
            EventFanoutFilterMode.EventTypeNoMatch => "event_type_no_match",
            EventFanoutFilterMode.IdentifierMatch => "identifier_match",
            EventFanoutFilterMode.IdentifierNoMatch => "identifier_no_match",
            _ => throw new ArgumentOutOfRangeException(nameof(filterMode), filterMode, "Unknown fanout filter mode."),
        };

    private static string ToMetricName(SignalRRealtimeFilterMode filterMode)
        => filterMode switch
        {
            SignalRRealtimeFilterMode.Unfiltered => "unfiltered",
            SignalRRealtimeFilterMode.EventTypeQueued => "event_type_queued",
            SignalRRealtimeFilterMode.DefinitionName => "definition_name",
            SignalRRealtimeFilterMode.Identifier => "identifier",
            _ => throw new ArgumentOutOfRangeException(nameof(filterMode), filterMode, "Unknown SignalR realtime filter mode."),
        };

    private static void AddQueueMetrics(
        ScenarioMetrics metrics,
        QueueOperationResult result,
        string prefix = "")
    {
        metrics.Add($"{prefix}accepted_workers", result.AcceptedWorkers, "workers");
        metrics.Add($"{prefix}rejected_workers", result.RejectedWorkers, "workers");
        metrics.Add($"{prefix}queue_elapsed_ms", result.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}accepted_per_sec", Rate(result.AcceptedWorkers, result.Elapsed), "workers/sec");
        AddDurationMetrics(metrics, $"{prefix}queue_latency", result.Latency);
    }

    private static void AddCompletionMetrics(
        ScenarioMetrics metrics,
        CompletionOperationResult result,
        string prefix = "")
    {
        metrics.Add($"{prefix}completion_workers", result.RequestedWorkers, "workers");
        metrics.Add($"{prefix}accepted_starts", result.AcceptedStarts, "workers");
        metrics.Add($"{prefix}completed_workers", result.CompletedWorkers, "workers");
        metrics.Add($"{prefix}completion_elapsed_ms", result.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}completed_per_sec", Rate(result.CompletedWorkers, result.Elapsed), "workers/sec");
        AddDurationMetrics(metrics, $"{prefix}start_action_latency", result.StartActionLatency);
        AddDurationMetrics(metrics, $"{prefix}completion_wait_latency", result.CompletionWaitLatency);
    }

    private static void AddReadModelMetrics(
        ScenarioMetrics metrics,
        ReadModelCatchupResult result,
        string prefix)
    {
        metrics.Add($"{prefix}_read_model_catchup_ms", result.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_read_model_sequence_backlog", result.Start.PendingUpdateCount, "updates");
        metrics.Add($"{prefix}_read_model_enqueued_sequence", result.End.EnqueuedSequence, "updates");
        metrics.Add($"{prefix}_read_model_applied_sequence", result.End.AppliedSequence, "updates");
        metrics.Add($"{prefix}_read_model_applied_delta", result.AppliedUpdateDelta, "updates");
        metrics.Add($"{prefix}_read_model_pending_after", result.End.PendingUpdateCount, "updates");
        metrics.Add($"{prefix}_read_model_last_batch_size", result.End.LastBatchSize, "updates");
        metrics.Add($"{prefix}_read_model_last_projection_ms", result.End.LastProjectionDuration.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}_read_model_published_snapshots_delta", result.PublishedSnapshotDelta, "snapshots");
    }

    private static void AddDiagnostics(
        ScenarioMetrics metrics,
        WorkSystemReadModelDiagnostics diagnostics)
    {
        metrics.Add("read_model_enqueued_sequence", diagnostics.EnqueuedSequence, "updates");
        metrics.Add("read_model_applied_sequence", diagnostics.AppliedSequence, "updates");
        metrics.Add("read_model_pending_updates", diagnostics.PendingUpdateCount, "updates");
        metrics.Add("read_model_applied_updates", diagnostics.AppliedUpdateCount, "updates");
        metrics.Add("read_model_published_snapshots", diagnostics.PublishedSnapshotCount, "snapshots");
        metrics.Add("read_model_last_batch_size", diagnostics.LastBatchSize, "updates");
        metrics.Add("read_model_last_projection_ms", diagnostics.LastProjectionDuration.TotalMilliseconds, "ms");
    }

    private static void AddDurationMetrics(
        ScenarioMetrics metrics,
        string prefix,
        DurationSnapshot snapshot)
    {
        metrics.Add($"{prefix}_count", snapshot.Count, "samples");
        metrics.Add($"{prefix}_mean_ms", snapshot.MeanMilliseconds, "ms");
        metrics.Add($"{prefix}_p50_ms", snapshot.P50Milliseconds, "ms");
        metrics.Add($"{prefix}_p95_ms", snapshot.P95Milliseconds, "ms");
        metrics.Add($"{prefix}_p99_ms", snapshot.P99Milliseconds, "ms");
        metrics.Add($"{prefix}_max_ms", snapshot.MaxMilliseconds, "ms");
    }

    private static void AddActionMetrics(
        ScenarioMetrics metrics,
        ActionOperationResult result,
        string prefix)
    {
        metrics.Add($"{prefix}action", result.Action.ToString(), "action");
        metrics.Add($"{prefix}requested_workers", result.RequestedWorkers, "workers");
        metrics.Add($"{prefix}accepted_workers", result.AcceptedWorkers, "workers");
        metrics.Add($"{prefix}rejected_workers", result.RejectedWorkers, "workers");
        metrics.Add($"{prefix}elapsed_ms", result.Elapsed.TotalMilliseconds, "ms");
        metrics.Add($"{prefix}accepted_per_sec", Rate(result.AcceptedWorkers, result.Elapsed), "workers/sec");
        AddDurationMetrics(metrics, $"{prefix}latency", result.Latency);
    }

    private static void AddMemoryMetrics(
        ScenarioMetrics metrics,
        string prefix,
        MemorySnapshot snapshot)
    {
        metrics.Add($"{prefix}_managed_bytes", snapshot.ManagedBytes, "bytes");
        metrics.Add($"{prefix}_private_bytes", snapshot.PrivateBytes, "bytes");
    }

    private static void AddDurabilityMetrics(
        ScenarioMetrics metrics,
        string prefix,
        WorkSystemDurabilityDiagnostics diagnostics)
    {
        metrics.Add($"{prefix}_accepted_waiter_count", diagnostics.AcceptedWaiterCount, "requests");
        metrics.Add($"{prefix}_pending_cleanup_count", diagnostics.PendingCleanupCount, "workers");
        metrics.Add($"{prefix}_reader_failure", diagnostics.HasReaderFailure.ToString(CultureInfo.InvariantCulture), "bool");
        metrics.Add($"{prefix}_lease_renewal_failure", diagnostics.HasLeaseRenewalFailure.ToString(CultureInfo.InvariantCulture), "bool");
        metrics.Add($"{prefix}_cleanup_failure", diagnostics.HasCleanupFailure.ToString(CultureInfo.InvariantCulture), "bool");
    }

    private static void AddDurableStateMetrics(
        ScenarioMetrics metrics,
        string prefix,
        DurableStateCounts counts)
    {
        metrics.Add($"{prefix}_work_entries", counts.WorkEntries, "rows");
        metrics.Add($"{prefix}_workflow_runs", counts.WorkflowRuns, "rows");
    }

    private static async Task<(string ConnectionString, string Description)> ResolveDurability(HarnessOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DurabilityConnectionString))
        {
            return (options.DurabilityConnectionString, "explicit connection string");
        }

        var sql = await BenchmarkSqlServerEnvironment.GetShared();
        return (sql.ConnectionString, sql.Description);
    }

    private static string QuoteIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static double Rate(long count, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0 ? 0 : count / elapsed.TotalSeconds;

    private static double PerWorker(long bytes, int workers)
        => workers <= 0 ? 0 : (double)bytes / workers;

    private sealed class HarnessSystem : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly WorkRequestContext requestContext;

        private HarnessSystem(ServiceProvider provider, IWorkSystem system, WorkRequestContext requestContext)
        {
            this.provider = provider;
            this.System = system;
            this.requestContext = requestContext;
        }

        public IWorkSystem System { get; }

        public static async Task<HarnessSystem> Create(
            HarnessOptions options,
            CancellationToken cancellationToken,
            Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>>? executor = null)
        {
            EnsureInMemory(options.QueueMode);
            var even = WorkDefinition.Create(EvenWorkName, category: "Perf:Even");
            var odd = WorkDefinition.Create(OddWorkName, category: "Perf:Odd");
            executor ??= CreateWorkExecutor(options.WorkDelay);
            var services = new ServiceCollection()
                .AddWorkableSystem(builder =>
                {
                    builder.RequireAuthorization(false);
                    builder.AddWork(even, executor);
                    builder.AddWork(odd, executor);
                });

            var provider = services.BuildServiceProvider();
            var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
            var context = WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                new WorkActor(Id: "workable.perf.scenario", Name: "Workable Scenario Benchmark"),
                "Run Workable performance harness scenario.");
            await system.Start(context, cancellationToken);
            return new HarnessSystem(provider, system, context);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.System.Stop(this.requestContext);
            }
            finally
            {
                await this.provider.DisposeAsync();
            }
        }
    }

    private sealed class ScenarioMetrics(string name)
    {
        private readonly List<ScenarioMetric> metrics = [];

        public string Name { get; } = name;

        public IReadOnlyList<ScenarioMetric> Items => this.metrics;

        public void Add(string metric, int value, string unit)
            => this.Add(metric, value.ToString(CultureInfo.InvariantCulture), unit);

        public void Add(string metric, long value, string unit)
            => this.Add(metric, value.ToString(CultureInfo.InvariantCulture), unit);

        public void Add(string metric, double value, string unit)
            => this.Add(metric, value.ToString("0.###", CultureInfo.InvariantCulture), unit);

        public void Add(string metric, string value, string unit)
            => this.metrics.Add(new ScenarioMetric(metric, value, unit));
    }

    private sealed record ScenarioMetric(
        string Name,
        string Value,
        string Unit);

    private sealed record QueueOperationResult(
        IReadOnlyList<IWorkerHandle> Handles,
        int AcceptedWorkers,
        int RejectedWorkers,
        TimeSpan Elapsed,
        DurationSnapshot Latency)
    {
        public static QueueOperationResult Empty { get; } = new(
            [],
            0,
            0,
            TimeSpan.Zero,
            new DurationSnapshot(0, 0, 0, 0, 0, 0));
    }

    private sealed record StartToCompletionOperationResult(
        int AcceptedWorkers,
        int RejectedWorkers,
        int CompletedWorkers,
        DurationSnapshot QueueLatency,
        DurationSnapshot CompletionWaitLatency)
    {
        public static StartToCompletionOperationResult Empty { get; } = new(
            0,
            0,
            0,
            new DurationSnapshot(0, 0, 0, 0, 0, 0),
            new DurationSnapshot(0, 0, 0, 0, 0, 0));
    }

    private sealed record CompletionOperationResult(
        int RequestedWorkers,
        int AcceptedStarts,
        int CompletedWorkers,
        TimeSpan Elapsed,
        DurationSnapshot StartActionLatency,
        DurationSnapshot CompletionWaitLatency)
    {
        public static CompletionOperationResult Empty { get; } = new(
            0,
            0,
            0,
            TimeSpan.Zero,
            new DurationSnapshot(0, 0, 0, 0, 0, 0),
            new DurationSnapshot(0, 0, 0, 0, 0, 0));
    }

    private sealed record ActionOperationResult(
        WorkAction Action,
        int RequestedWorkers,
        int AcceptedWorkers,
        int RejectedWorkers,
        TimeSpan Elapsed,
        DurationSnapshot Latency)
    {
        public static ActionOperationResult Empty { get; } = new(
            WorkAction.Start,
            0,
            0,
            0,
            TimeSpan.Zero,
            new DurationSnapshot(0, 0, 0, 0, 0, 0));
    }

    private readonly record struct MemorySnapshot(
        long ManagedBytes,
        long PrivateBytes);

    private readonly record struct DurableStateCounts(
        long WorkEntries,
        long WorkflowRuns);

    private sealed record ReadModelCatchupResult(
        WorkSystemReadModelDiagnostics Start,
        WorkSystemReadModelDiagnostics End,
        TimeSpan Elapsed)
    {
        public long AppliedUpdateDelta => this.End.AppliedUpdateCount - this.Start.AppliedUpdateCount;

        public long PublishedSnapshotDelta => this.End.PublishedSnapshotCount - this.Start.PublishedSnapshotCount;
    }

    private sealed record EventSubscriptionDiagnosticsSummary(
        int Subscriptions,
        int QueuedEvents,
        int PeakQueuedEvents,
        long AcceptedEvents,
        long DeliveredEvents,
        long DroppedEvents);

    private sealed record SubscriptionChurnLoopResult(
        int Cycles,
        long SubscribeOperations,
        long UnsubscribeOperations,
        DurationSnapshot SubscribeLatency,
        DurationSnapshot UnsubscribeLatency);

    private sealed record LifecyclePassResult(
        QueueOperationResult Queue,
        int CompletedWorkers,
        TimeSpan Elapsed,
        DurationSnapshot CompletionLatency,
        ReadModelCatchupResult ReadModelCatchup,
        int SubscriptionCount,
        EventFanoutFilterMode FilterMode,
        long AllocatedBytes,
        EventSubscriptionDiagnosticsSummary SubscriptionDiagnostics)
    {
        public double CompletedPerSecond => Rate(this.CompletedWorkers, this.Elapsed);
    }

    private sealed record DeliveryPassResult(
        QueueOperationResult Queue,
        int CompletedWorkers,
        TimeSpan Elapsed,
        DurationSnapshot CompletionLatency,
        ReadModelCatchupResult ReadModelCatchup,
        int SubscriptionCount,
        EventFanoutFilterMode FilterMode,
        long AllocatedBytes,
        EventSubscriptionDiagnosticsSummary SubscriptionDiagnostics,
        long ObservedEvents)
    {
        public double CompletedPerSecond => Rate(this.CompletedWorkers, this.Elapsed);

        public long DeliveredEvents => this.SubscriptionDiagnostics.DeliveredEvents;

        public long DroppedEvents => this.SubscriptionDiagnostics.DroppedEvents;

        public double DeliveredEventsPerSecond => Rate(this.DeliveredEvents, this.Elapsed);
    }

    private sealed record SignalRRealtimePassResult(
        QueueOperationResult Queue,
        int SubscriptionCount,
        SignalRRealtimeFilterMode FilterMode,
        TimeSpan TotalElapsed,
        TimeSpan DeliveryWaitElapsed,
        DurationSnapshot ConnectLatency,
        DurationSnapshot WatchLatency,
        long ObservedEvents)
    {
        public long ExpectedEvents => (long)this.Queue.AcceptedWorkers * this.SubscriptionCount;

        public double DeliveredEventsPerSecond => Rate(this.ObservedEvents, this.TotalElapsed);
    }

    private enum EventFanoutFilterMode
    {
        Unfiltered,
        EventTypeCompleted,
        EventTypeNoMatch,
        IdentifierMatch,
        IdentifierNoMatch,
    }

    private enum SignalRRealtimeFilterMode
    {
        Unfiltered,
        EventTypeQueued,
        DefinitionName,
        Identifier,
    }

    private sealed class SignalRRealtimeCounter(string eventType)
    {
        private readonly object sync = new();
        private TaskCompletionSource completion = NewCompletion();
        private int expectedCount;
        private int observedCount;

        public int ExpectedCount
        {
            get
            {
                lock (this.sync)
                {
                    return this.expectedCount;
                }
            }
        }

        public int ObservedCount
        {
            get
            {
                lock (this.sync)
                {
                    return this.observedCount;
                }
            }
        }

        public void Reset(int expectedEvents)
        {
            lock (this.sync)
            {
                this.expectedCount = expectedEvents;
                this.observedCount = 0;
                this.completion = NewCompletion();
                if (expectedEvents <= 0)
                {
                    this.completion.TrySetResult();
                }
            }
        }

        public void Observe(WorkableRealtimeEvent workEvent)
        {
            if (!string.Equals(workEvent.EventType, eventType, StringComparison.Ordinal))
            {
                return;
            }

            TaskCompletionSource? signal = null;
            lock (this.sync)
            {
                if (this.observedCount >= this.expectedCount)
                {
                    return;
                }

                this.observedCount++;
                if (this.observedCount >= this.expectedCount)
                {
                    signal = this.completion;
                }
            }

            signal?.TrySetResult();
        }

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => this.completion.Task.WaitAsync(timeout, cancellationToken);

        private static TaskCompletionSource NewCompletion()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LifecycleStageRecorder(int workerCount)
    {
        private readonly long[] queueStarted = new long[workerCount];
        private readonly long[] queueCompleted = new long[workerCount];
        private readonly long[] startActionRequested = new long[workerCount];
        private readonly long[] executorStarted = new long[workerCount];
        private readonly long[] executorCompleted = new long[workerCount];
        private readonly long[] completionObserved = new long[workerCount];
        private readonly TaskCompletionSource[] executorStartedSignals =
            Enumerable.Range(0, workerCount)
                .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();

        public void MarkQueueStarted(int index)
            => Volatile.Write(ref this.queueStarted[index], Stopwatch.GetTimestamp());

        public void MarkQueueCompleted(int index)
            => Volatile.Write(ref this.queueCompleted[index], Stopwatch.GetTimestamp());

        public void MarkStartActionRequested(int index)
            => Volatile.Write(ref this.startActionRequested[index], Stopwatch.GetTimestamp());

        public void MarkExecutorStarted(int index)
        {
            Volatile.Write(ref this.executorStarted[index], Stopwatch.GetTimestamp());
            this.executorStartedSignals[index].TrySetResult();
        }

        public void MarkExecutorCompleted(int index)
            => Volatile.Write(ref this.executorCompleted[index], Stopwatch.GetTimestamp());

        public void MarkCompletionObserved(int index)
            => Volatile.Write(ref this.completionObserved[index], Stopwatch.GetTimestamp());

        public Task WaitForExecutorStarted(int index, CancellationToken cancellationToken)
            => this.executorStartedSignals[index].Task.WaitAsync(cancellationToken);

        public DurationSnapshot SnapshotQueueStartToExecutorStart()
            => this.Snapshot(this.queueStarted, this.executorStarted);

        public DurationSnapshot SnapshotStartActionToExecutorStart()
            => this.Snapshot(this.startActionRequested, this.executorStarted);

        public DurationSnapshot SnapshotExecutorDuration()
            => this.Snapshot(this.executorStarted, this.executorCompleted);

        public DurationSnapshot SnapshotExecutorEndToCompletionObserved()
            => this.Snapshot(this.executorCompleted, this.completionObserved);

        public DurationSnapshot SnapshotStartToCompletion()
            => this.Snapshot(this.queueStarted, this.completionObserved);

        private DurationSnapshot Snapshot(long[] startTicks, long[] endTicks)
        {
            var samples = new List<double>(startTicks.Length);
            for (var index = 0; index < startTicks.Length; index++)
            {
                var started = Volatile.Read(ref startTicks[index]);
                var ended = Volatile.Read(ref endTicks[index]);
                if (started <= 0 || ended < started)
                {
                    continue;
                }

                samples.Add((ended - started) * 1000.0 / Stopwatch.Frequency);
            }

            if (samples.Count == 0)
            {
                return new DurationSnapshot(0, 0, 0, 0, 0, 0);
            }

            samples.Sort();
            return new DurationSnapshot(
                samples.Count,
                samples.Average(),
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                Percentile(samples, 0.99),
                samples[^1]);
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }
}
