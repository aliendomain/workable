using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Workable;

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
        "event-fanout",
    ];

    public static async Task Run(HarnessOptions options, CancellationToken cancellationToken = default)
    {
        if (options.QueueMode != HarnessQueueMode.InMemory)
        {
            throw new InvalidOperationException(
                "Named scenario benchmarks currently target the in-memory backend. Use the legacy lifecycle-fanout scenario for durable queue modes.");
        }

        var scenarios = ResolveScenarios(options.Scenario);
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
            }
        }
    }

    private static async Task<ScenarioMetrics> RunScenario(
        string scenario,
        HarnessOptions options,
        CancellationToken cancellationToken)
        => scenario switch
        {
            "queue-only" => await RunQueueOnly(scenario, options, cancellationToken),
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
            "event-fanout" or "event-fanout-matrix" => await RunEventFanout(scenario, options, cancellationToken),
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
        var beforeManaged = GC.GetTotalMemory(forceFullCollection: true);
        var beforePrivate = Process.GetCurrentProcess().PrivateMemorySize64;

        var queued = await QueueWorkers(
            harness.System,
            options.Workers,
            DoNotStartOptions,
            options,
            scenario,
            startIndex: 0,
            cancellationToken);
        var catchup = await WaitForReadModel(harness.System, cancellationToken);

        var afterManaged = GC.GetTotalMemory(forceFullCollection: true);
        var afterPrivate = Process.GetCurrentProcess().PrivateMemorySize64;
        var managedDelta = afterManaged - beforeManaged;
        var privateDelta = afterPrivate - beforePrivate;

        AddQueueMetrics(metrics, queued);
        AddReadModelMetrics(metrics, catchup, "post_memory_growth");
        metrics.Add("managed_memory_before_bytes", beforeManaged, "bytes");
        metrics.Add("managed_memory_after_bytes", afterManaged, "bytes");
        metrics.Add("managed_memory_delta_bytes", managedDelta, "bytes");
        metrics.Add("managed_memory_delta_per_worker_bytes", PerWorker(managedDelta, queued.AcceptedWorkers), "bytes/worker");
        metrics.Add("private_memory_before_bytes", beforePrivate, "bytes");
        metrics.Add("private_memory_after_bytes", afterPrivate, "bytes");
        metrics.Add("private_memory_delta_bytes", privateDelta, "bytes");
        metrics.Add("private_memory_delta_per_worker_bytes", PerWorker(privateDelta, queued.AcceptedWorkers), "bytes/worker");
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

    private static HarnessQueueMode EnsureInMemory(HarnessQueueMode mode)
        => mode == HarnessQueueMode.InMemory
            ? mode
            : throw new InvalidOperationException("Scenario benchmarks support only the in-memory queue mode.");

    private static string[] ResolveScenarios(string scenario)
    {
        var normalized = NormalizeScenario(scenario);
        if (normalized == "all")
        {
            return AllScenarios;
        }

        if (normalized == "event-fanout-matrix")
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

    private enum EventFanoutFilterMode
    {
        Unfiltered,
        EventTypeCompleted,
        EventTypeNoMatch,
        IdentifierMatch,
        IdentifierNoMatch,
    }

    private sealed class LifecycleStageRecorder(int workerCount)
    {
        private readonly long[] queueStarted = new long[workerCount];
        private readonly long[] queueCompleted = new long[workerCount];
        private readonly long[] executorStarted = new long[workerCount];
        private readonly long[] executorCompleted = new long[workerCount];
        private readonly long[] completionObserved = new long[workerCount];

        public void MarkQueueStarted(int index)
            => Volatile.Write(ref this.queueStarted[index], Stopwatch.GetTimestamp());

        public void MarkQueueCompleted(int index)
            => Volatile.Write(ref this.queueCompleted[index], Stopwatch.GetTimestamp());

        public void MarkExecutorStarted(int index)
            => Volatile.Write(ref this.executorStarted[index], Stopwatch.GetTimestamp());

        public void MarkExecutorCompleted(int index)
            => Volatile.Write(ref this.executorCompleted[index], Stopwatch.GetTimestamp());

        public void MarkCompletionObserved(int index)
            => Volatile.Write(ref this.completionObserved[index], Stopwatch.GetTimestamp());

        public DurationSnapshot SnapshotQueueStartToExecutorStart()
            => this.Snapshot(this.queueStarted, this.executorStarted);

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
