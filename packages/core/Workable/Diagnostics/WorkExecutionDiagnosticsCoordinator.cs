using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed class WorkExecutionDiagnosticsCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan AbandonedIterationAge = TimeSpan.FromDays(1);
    private const int MaximumCaptureRuleDefinitionNameLength = 450;
    private const int MaximumStructuredPropertyCount = 64;
    private readonly WorkSystemId workSystemId;
    private readonly string? workSystemName;
    private readonly IWorkExecutionDiagnosticsRepository? repository;
    private readonly WorkExecutionDiagnosticsPolicyResolver policies;
    private readonly WorkSystemExecutionDiagnosticsPersistenceConfiguration configuration;
    private readonly ILogger? logger;
    private readonly Channel<PersistenceOperation> channel;
    private readonly ConcurrentDictionary<WorkerIterationReference, CaptureState> captures = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool isProduction;
    private readonly WorkExecutionDiagnosticInstrumentationAvailability instrumentationAvailability;
    private readonly SemaphoreSlim ruleMutation = new(1, 1);
    private CaptureRuleIndex captureRules = CaptureRuleIndex.Empty;
    private Task? writerTask;
    private Task? cleanupTask;
    private int initialized;
    private int pendingEvidenceOperations;
    private int pendingProfiles;
    private long pendingLogBytes;

    public WorkExecutionDiagnosticsCoordinator(
        WorkSystemId workSystemId,
        string? workSystemName,
        IWorkExecutionDiagnosticsRepository? repository,
        WorkSystemExecutionDiagnosticsPersistenceConfiguration configuration,
        ILogger? logger,
        bool isProduction = true,
        WorkExecutionDiagnosticInstrumentationAvailability? instrumentationAvailability = null)
    {
        this.workSystemId = workSystemId;
        this.workSystemName = workSystemName;
        this.repository = repository;
        this.configuration = configuration;
        this.policies = new WorkExecutionDiagnosticsPolicyResolver(configuration);
        this.logger = logger;
        this.isProduction = isProduction;
        this.instrumentationAvailability = instrumentationAvailability ?? new(false, false);
        this.channel = Channel.CreateBounded<PersistenceOperation>(new BoundedChannelOptions(
            checked(configuration.ChannelCapacity + configuration.ControlOperationCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public bool IsAvailable => this.repository is not null;

    public WorkExecutionDiagnosticsPolicy? ResolvePolicy(
        WorkConfiguration workConfiguration,
        string definitionName)
    {
        var now = DateTimeOffset.UtcNow;
        var rule = Volatile.Read(ref this.captureRules).Resolve(definitionName, now);
        if (rule is not null)
        {
            return new WorkExecutionDiagnosticsPolicy(
                rule.ArtifactRetention,
                rule.MinimumLogLevel,
                rule.ProfileCaptureMode,
                rule.DefinitionName is null
                    ? WorkExecutionDiagnosticCaptureSource.TemporarySystemRule
                    : WorkExecutionDiagnosticCaptureSource.TemporaryWorkRule);
        }

        return this.policies.Resolve(workConfiguration);
    }

    public bool ShouldEnableProfiling(WorkExecutionDiagnosticsPolicy policy)
        => policy.ProfileCaptureMode is not null &&
            (!this.isProduction || policy.CaptureSource is
                WorkExecutionDiagnosticCaptureSource.TemporarySystemRule or
                WorkExecutionDiagnosticCaptureSource.TemporaryWorkRule);

    public WorkProfileCaptureMode? ResolveProfileCaptureMode(WorkerRecord worker)
    {
        var reference = worker.GetCurrentIterationReference();
        if (this.captures.TryGetValue(reference, out var state) &&
            this.ShouldEnableProfiling(state.Policy) &&
            state.Policy.ProfileCaptureMode is { } requestedMode)
        {
            return worker.Options.ProfilingEnabled &&
                worker.Options.ProfilingCaptureMode == WorkProfileCaptureMode.Full
                    ? WorkProfileCaptureMode.Full
                    : requestedMode;
        }

        return worker.Options.ProfilingEnabled ? worker.Options.ProfilingCaptureMode : null;
    }

    public async Task Initialize(
        IReadOnlyList<WorkDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var required = this.configuration.IsEnabled ||
            definitions.Any(definition => definition.Configuration.ExecutionDiagnostics.IsEnabled == true);
        if (required && this.repository is null)
        {
            throw new InvalidOperationException(
                "Persistent execution diagnostics require a registered IWorkExecutionDiagnosticsRepository.");
        }

        if (this.repository is null || Volatile.Read(ref this.initialized) != 0)
        {
            return;
        }

        await this.repository.Initialize(
            new WorkExecutionDiagnosticsInitializationContext(this.workSystemId, this.workSystemName),
            cancellationToken);
        await this.RefreshCaptureRules(cancellationToken);
        this.writerTask = Task.Run(this.RunWriter);
        this.cleanupTask = Task.Run(this.RunCleanup);
        Volatile.Write(ref this.initialized, 1);
    }

    public void ObserveIteration(WorkerRecord worker, WorkerIterationSnapshot iteration)
    {
        if (this.repository is null || Volatile.Read(ref this.initialized) == 0)
        {
            return;
        }

        var reference = new WorkerIterationReference(worker.Id, iteration.Sequence);
        if (iteration.Status == WorkCompletionStatus.Executing)
        {
            var policy = this.ResolvePolicy(worker.Configuration, worker.Work.Definition.Name);
            if (policy is null || this.captures.ContainsKey(reference))
            {
                return;
            }

            var state = new CaptureState(Guid.NewGuid(), policy);
            if (!this.captures.TryAdd(reference, state))
            {
                return;
            }

            var start = new WorkExecutionDiagnosticIterationStart(
                state.DiagnosticId,
                this.workSystemId,
                this.workSystemName,
                worker.Id,
                iteration.Sequence,
                worker.Work.Definition.Id,
                worker.Work.Definition.Name,
                iteration.StartedAt,
                policy.Retention,
                policy.MinimumLogLevel,
                policy.ProfileCaptureMode is null ? null : this.ResolveProfileCaptureMode(worker),
                this.instrumentationAvailability,
                policy.CaptureSource);
            if (!this.TryWrite(new BeginOperation(start)))
            {
                this.captures.TryRemove(reference, out _);
                this.logger?.LogWarning(
                    "Persistent execution diagnostics were dropped for worker {WorkerId} iteration {IterationSequence} because the bounded writer queue was full.",
                    worker.Id,
                    iteration.Sequence);
            }

            return;
        }

        if (!iteration.Status.IsFinal() ||
            !this.captures.TryGetValue(reference, out var completed) ||
            !completed.TryBeginCompletion())
        {
            return;
        }

        var completion = new IterationCompletion(
            iteration.Status,
            iteration.AttemptCount,
            iteration.CompletedAt,
            iteration.ExecutionDuration,
            completed.Policy.ProfileCaptureMode is not null && !completed.ProfileExpected
                ? iteration.Profile
                : null);
        if (!this.TryWrite(new CompleteOperation(reference, completed, completion)))
        {
            this.captures.TryRemove(reference, out _);
            this.logger?.LogWarning(
                "Persistent execution diagnostics completion was dropped for worker {WorkerId} iteration {IterationSequence} because the bounded writer queue was full.",
                worker.Id,
                iteration.Sequence);
        }
    }

    public bool IsLogEnabled(WorkerRecord worker, LogLevel level)
    {
        var reference = worker.GetCurrentIterationReference();
        return this.captures.TryGetValue(reference, out var state) &&
            level != LogLevel.None &&
            level >= state.Policy.MinimumLogLevel;
    }

    public void CaptureLog<TState>(
        WorkerRecord worker,
        DateTimeOffset occurredAt,
        string category,
        LogLevel level,
        EventId eventId,
        WorkerLogEntry? retainedEntry,
        TState logState,
        Exception? exception,
        Func<TState, Exception?, string> formatter,
        ActivityTraceId? traceId,
        ActivitySpanId? spanId)
    {
        var reference = worker.GetCurrentIterationReference();
        if (!this.captures.TryGetValue(reference, out var state) ||
            level < state.Policy.MinimumLogLevel ||
            !state.MayAcceptLog(
                this.configuration.MaximumLogsPerIteration,
                this.configuration.MaximumLogBytesPerIteration))
        {
            return;
        }

        var ordinal = state.NextOrdinal();
        LogOperation operation;
        try
        {
            var message = Truncate(
                retainedEntry?.Message ?? formatter(logState, exception),
                this.configuration.MaximumLogMessageLength);
            operation = new LogOperation(
                state,
                new WorkExecutionDiagnosticLogRecord(
                    state.DiagnosticId,
                    ordinal,
                    occurredAt,
                    level,
                    Truncate(category, 512),
                    new EventId(eventId.Id, TruncateNullable(eventId.Name, 256)),
                    message,
                    null,
                    TruncateNullable(retainedEntry?.ExceptionType ?? exception?.GetType().FullName, 512),
                    TruncateNullable(retainedEntry?.ExceptionMessage ?? exception?.Message, this.configuration.MaximumExceptionTextLength),
                    TruncateNullable(exception?.StackTrace, this.configuration.MaximumExceptionTextLength),
                    traceId?.ToHexString(),
                    spanId?.ToHexString()),
                CaptureProperties(logState, this.configuration.MaximumLogPropertiesLength));
        }
        catch (Exception materializationException) when (materializationException is not (OutOfMemoryException or StackOverflowException))
        {
            state.RecordDrop();
            this.logger?.LogWarning(materializationException, "A persistent execution diagnostics log could not be bounded and captured.");
            return;
        }

        if (!state.TryAcceptLog(
            this.configuration.MaximumLogsPerIteration,
            this.configuration.MaximumLogBytesPerIteration,
            operation.EstimatedBytes))
        {
            return;
        }

        if (this.TryWriteLog(operation))
        {
            state.RecordPersisted();
        }
        else
        {
            state.RecordQueueDrop(operation.EstimatedBytes);
        }
    }

    public bool TryCaptureProfile(WorkerRecord worker, WorkProfile profile)
    {
        var reference = worker.GetCurrentIterationReference();
        if (!this.captures.TryGetValue(reference, out var state) ||
            state.Policy.ProfileCaptureMode is null)
        {
            return false;
        }

        profile.Seal();
        state.MarkProfileExpected();
        var preserveWorkerProfile = worker.Options.ProfilingEnabled;
        if (!this.TryWriteProfile(new ProfileOperation(
            state,
            worker,
            reference.Sequence,
            profile,
            preserveWorkerProfile)))
        {
            state.MarkProfileDropped();
            return !preserveWorkerProfile;
        }

        return true;
    }

    public async Task Flush(CancellationToken cancellationToken = default)
    {
        if (this.writerTask is null)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (!this.channel.Writer.TryWrite(new FlushOperation(completion)))
        {
            await Task.Delay(10, cancellationToken);
        }

        await completion.Task.WaitAsync(cancellationToken);
    }

    public Task<WorkExecutionDiagnosticQueryResult> Query(
        WorkExecutionDiagnosticCriteria criteria,
        CancellationToken cancellationToken)
        => this.repository is null
            ? Task.FromResult(new WorkExecutionDiagnosticQueryResult([]))
            : this.repository.Query(criteria, cancellationToken);

    public Task<WorkExecutionDiagnosticArtifact?> Get(
        WorkExecutionDiagnosticGetRequest request,
        CancellationToken cancellationToken)
        => this.repository is null
            ? Task.FromResult<WorkExecutionDiagnosticArtifact?>(null)
            : this.repository.Get(request, cancellationToken);

    public IReadOnlyList<WorkExecutionDiagnosticCaptureRule> GetCaptureRules()
        => Volatile.Read(ref this.captureRules).GetActive(DateTimeOffset.UtcNow);

    public async Task<WorkExecutionDiagnosticCaptureRule> CreateCaptureRule(
        string? definitionName,
        LogLevel minimumLogLevel,
        WorkProfileCaptureMode? profileCaptureMode,
        TimeSpan activeFor,
        TimeSpan artifactRetention,
        WorkActor createdBy,
        CancellationToken cancellationToken)
    {
        if (this.repository is null)
        {
            throw new InvalidOperationException("Persistent execution diagnostics are not available for this system.");
        }

        ValidateLifetime(activeFor, nameof(activeFor));
        ValidateLifetime(artifactRetention, nameof(artifactRetention));
        if (minimumLogLevel == LogLevel.None || !Enum.IsDefined(minimumLogLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLogLevel), "A persistent log level is required.");
        }

        if (profileCaptureMode is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(profileCaptureMode), "A valid profile capture mode is required.");
        }

        var normalizedDefinitionName = string.IsNullOrWhiteSpace(definitionName) ? null : definitionName.Trim();
        if (normalizedDefinitionName?.Length > MaximumCaptureRuleDefinitionNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definitionName),
                $"Capture rule definition names cannot exceed {MaximumCaptureRuleDefinitionNameLength} characters.");
        }

        await this.ruleMutation.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref this.captureRules);
            if (current.GetActive(DateTimeOffset.UtcNow).Count >= this.configuration.MaximumCaptureRules)
            {
                throw new InvalidOperationException(
                    $"A work system cannot have more than {this.configuration.MaximumCaptureRules} active execution diagnostic capture rules.");
            }

            var now = DateTimeOffset.UtcNow;
            var rule = new WorkExecutionDiagnosticCaptureRule(
                Guid.NewGuid(),
                this.workSystemId,
                this.workSystemName,
                normalizedDefinitionName,
                minimumLogLevel,
                profileCaptureMode,
                artifactRetention,
                now,
                now + activeFor,
                createdBy);
            await this.repository.UpsertCaptureRule(rule, this.configuration.MaximumCaptureRules, cancellationToken);
            Volatile.Write(ref this.captureRules, current.WithUpsert(rule));
            return rule;
        }
        finally
        {
            this.ruleMutation.Release();
        }
    }

    public async Task<bool> DeleteCaptureRule(Guid id, CancellationToken cancellationToken)
    {
        if (this.repository is null)
        {
            return false;
        }

        await this.ruleMutation.WaitAsync(cancellationToken);
        try
        {
            var deleted = await this.repository.DeleteCaptureRule(
                new WorkExecutionDiagnosticCaptureRuleDeleteRequest(this.workSystemId, id),
                cancellationToken);
            if (deleted)
            {
                Volatile.Write(ref this.captureRules, Volatile.Read(ref this.captureRules).WithDelete(id));
            }

            return deleted;
        }
        finally
        {
            this.ruleMutation.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.channel.Writer.TryComplete();
        this.lifetime.Cancel();
        if (this.writerTask is not null)
        {
            await IgnoreCancellation(this.writerTask);
        }

        if (this.cleanupTask is not null)
        {
            await IgnoreCancellation(this.cleanupTask);
        }

        this.lifetime.Dispose();
        this.ruleMutation.Dispose();
    }

    private bool TryWrite(PersistenceOperation operation)
        => this.channel.Writer.TryWrite(operation);

    private bool TryWriteLog(LogOperation operation)
    {
        var size = operation.EstimatedBytes;
        if (Interlocked.Add(ref this.pendingLogBytes, size) > this.configuration.MaximumPendingLogBytes)
        {
            Interlocked.Add(ref this.pendingLogBytes, -size);
            return false;
        }

        if (this.TryWriteEvidence(operation))
        {
            return true;
        }

        Interlocked.Add(ref this.pendingLogBytes, -size);
        return false;
    }

    private bool TryWriteEvidence(PersistenceOperation operation)
    {
        if (Interlocked.Increment(ref this.pendingEvidenceOperations) > this.configuration.ChannelCapacity)
        {
            Interlocked.Decrement(ref this.pendingEvidenceOperations);
            return false;
        }

        if (this.channel.Writer.TryWrite(operation))
        {
            return true;
        }

        Interlocked.Decrement(ref this.pendingEvidenceOperations);
        return false;
    }

    private bool TryWriteProfile(ProfileOperation operation)
    {
        var maximumPendingProfiles = Math.Min(
            this.configuration.MaximumPendingProfiles,
            this.configuration.ChannelCapacity);
        if (Interlocked.Increment(ref this.pendingProfiles) > maximumPendingProfiles)
        {
            Interlocked.Decrement(ref this.pendingProfiles);
            return false;
        }

        if (this.TryWriteEvidence(operation))
        {
            return true;
        }

        Interlocked.Decrement(ref this.pendingProfiles);
        return false;
    }

    private async Task RunWriter()
    {
        var logs = new List<LogOperation>(this.configuration.LogBatchSize);
        try
        {
            while (await this.channel.Reader.WaitToReadAsync())
            {
                while (this.channel.Reader.TryRead(out var operation))
                {
                    if (operation is LogOperation log)
                    {
                        logs.Add(log);

                        if (logs.Count >= this.configuration.LogBatchSize)
                        {
                            await this.WriteLogs(logs);
                        }

                        continue;
                    }

                    await this.WriteLogs(logs);
                    await this.Execute(operation);
                    if (operation is ProfileOperation)
                    {
                        Interlocked.Decrement(ref this.pendingEvidenceOperations);
                        Interlocked.Decrement(ref this.pendingProfiles);
                    }
                }

                await this.WriteLogs(logs);
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            this.logger?.LogError(exception, "Persistent execution diagnostics writer stopped unexpectedly.");
        }
        finally
        {
            await this.WriteLogs(logs);
        }
    }

    private async Task Execute(PersistenceOperation operation)
    {
        try
        {
            switch (operation)
            {
                case BeginOperation begin:
                    await this.repository!.BeginIteration(begin.Start, this.lifetime.Token);
                    break;
                case ProfileOperation profile:
                    try
                    {
                        var snapshot = profile.Profile.ToSnapshot();
                        if (profile.PreserveWorkerProfile)
                        {
                            profile.Worker.RecordIterationProfile(profile.IterationSequence, snapshot);
                        }

                        if (!this.IsProfileWithinBounds(snapshot))
                        {
                            profile.State.MarkProfileDropped();
                            break;
                        }

                        profile.State.SetProfile(snapshot);
                        if (!profile.PreserveWorkerProfile)
                        {
                            profile.Worker.RecordIterationProfile(profile.IterationSequence, snapshot);
                        }
                    }
                    catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                    {
                        profile.State.MarkProfileDropped();
                        this.logger?.LogWarning(exception, "A persistent execution diagnostics profile could not be materialized.");
                    }

                    break;
                case CompleteOperation complete:
                    var state = complete.State;
                    var completionData = complete.Completion;
                    var capturedProfile = state.ProfileExpected ? state.Profile : completionData.Profile;
                    if (capturedProfile is not null && !this.IsProfileWithinBounds(capturedProfile))
                    {
                        capturedProfile = null;
                        state.MarkProfileDropped();
                    }
                    var completion = new WorkExecutionDiagnosticIterationCompletion(
                        state.DiagnosticId,
                        completionData.Status,
                        completionData.AttemptCount,
                        completionData.CompletedAt,
                        completionData.ExecutionDuration,
                        capturedProfile,
                        state.ProfileDropped,
                        state.PersistedLogCount,
                        state.DroppedLogCount,
                        CreateInstrumentationSummary(capturedProfile));
                    await this.repository!.CompleteIteration(completion, this.lifetime.Token);
                    break;
                case FlushOperation flush:
                    flush.Completion.TrySetResult();
                    break;
            }
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            this.logger?.LogWarning(exception, "A persistent execution diagnostics operation failed.");
            if (operation is FlushOperation flush)
            {
                flush.Completion.TrySetException(exception);
            }
        }
        finally
        {
            if (operation is CompleteOperation complete)
            {
                this.captures.TryRemove(complete.Reference, out _);
            }
        }
    }

    private async Task WriteLogs(List<LogOperation> logs)
    {
        if (logs.Count == 0)
        {
            return;
        }

        var batch = logs.ToArray();
        logs.Clear();
        try
        {
            var records = batch.Select(log => log.Materialize()).ToArray();
            await this.repository!.AppendLogs(records, this.lifetime.Token);
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            foreach (var log in batch)
            {
                log.Capture.RecordMaterializationDrop();
            }

            this.logger?.LogWarning(exception, "A persistent execution diagnostics log batch failed.");
        }
        finally
        {
            Interlocked.Add(ref this.pendingLogBytes, -batch.Sum(log => log.EstimatedBytes));
            Interlocked.Add(ref this.pendingEvidenceOperations, -batch.Length);
        }
    }

    private async Task RunCleanup()
    {
        using var timer = new PeriodicTimer(this.configuration.CleanupInterval);
        var backlogRemaining = false;
        try
        {
            while (true)
            {
                if (backlogRemaining)
                {
                    await Task.Delay(this.configuration.CleanupBacklogDelay, this.lifetime.Token);
                }
                else if (!await timer.WaitForNextTickAsync(this.lifetime.Token))
                {
                    break;
                }

                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var activeIds = this.captures.Values
                        .Select(capture => capture.DiagnosticId)
                        .ToHashSet();
                    backlogRemaining = false;
                    for (var batch = 0; batch < this.configuration.MaximumCleanupBatchesPerInterval; batch++)
                    {
                        var deleted = await this.repository!.DeleteExpired(
                            new WorkExecutionDiagnosticsExpirationRequest(
                                this.workSystemId,
                                now,
                                now - AbandonedIterationAge,
                                this.configuration.CleanupBatchSize)
                            {
                                ActiveDiagnosticIds = activeIds,
                            },
                            this.lifetime.Token);
                        backlogRemaining = deleted >= this.configuration.CleanupBatchSize;
                        if (deleted < this.configuration.CleanupBatchSize)
                        {
                            break;
                        }
                    }

                    await this.RefreshCaptureRules(this.lifetime.Token);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    backlogRemaining = false;
                    this.logger?.LogWarning(exception, "Persistent execution diagnostics cleanup failed; it will be retried.");
                }
            }
        }
        catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCaptureRules(CancellationToken cancellationToken)
    {
        await this.ruleMutation.WaitAsync(cancellationToken);
        try
        {
            var rules = await this.repository!.ListCaptureRules(
                new WorkExecutionDiagnosticsInitializationContext(this.workSystemId, this.workSystemName),
                cancellationToken);
            Volatile.Write(ref this.captureRules, CaptureRuleIndex.Create(rules));
        }
        finally
        {
            this.ruleMutation.Release();
        }
    }

    private static void ValidateLifetime(TimeSpan value, string parameterName)
    {
        if (value < WorkExecutionDiagnosticsPersistenceConfiguration.MinimumRetention ||
            value > WorkExecutionDiagnosticsPersistenceConfiguration.MaximumRetention)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Execution diagnostic capture and retention periods must be between one minute and 30 days.");
        }
    }

    private static IReadOnlyList<WorkExecutionInstrumentationSummary> CreateInstrumentationSummary(
        WorkProfileSnapshot? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var accumulators = new Dictionary<string, InstrumentationAccumulator>(StringComparer.Ordinal);
        var pending = new Stack<WorkProfileSnapshotNode>();
        pending.Push(profile.Root);
        while (pending.TryPop(out var node))
        {
            var key = string.IsNullOrWhiteSpace(node.Instrumentation)
                ? WorkProfileInstrumentation.Application
                : node.Instrumentation;
            if (!accumulators.TryGetValue(key, out var accumulator))
            {
                accumulator = new InstrumentationAccumulator();
                accumulators.Add(key, accumulator);
            }

            accumulator.NodeCount++;
            if (node.MetricType == WorkProfileMetricType.Timing)
            {
                accumulator.TimingCount++;
                accumulator.TotalTimingMilliseconds += node.TreeMilliseconds;
                accumulator.MaximumTimingMilliseconds = Math.Max(
                    accumulator.MaximumTimingMilliseconds,
                    node.TreeMilliseconds);
            }

            AddOmittedInstrumentation(node, accumulators);

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        return [.. accumulators
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new WorkExecutionInstrumentationSummary(
                entry.Key,
                entry.Value.NodeCount,
                entry.Value.TimingCount,
                entry.Value.TotalTimingMilliseconds,
                entry.Value.MaximumTimingMilliseconds,
                entry.Value.OmittedNodeCount))];
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> CaptureProperties<TState>(
        TState state,
        int maximumLength)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            return [];
        }

        var captured = new List<KeyValuePair<string, object?>>(Math.Min(MaximumStructuredPropertyCount, 8));
        var remaining = maximumLength;
        var truncated = false;
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, "{OriginalFormat}", StringComparison.Ordinal))
            {
                continue;
            }

            if (captured.Count >= MaximumStructuredPropertyCount || remaining <= 0)
            {
                truncated = true;
                break;
            }

            var key = Truncate(property.Key, Math.Min(256, remaining));
            remaining -= key.Length;
            var value = CapturePropertyValue(property.Value, remaining, out var valueLength, out var valueTruncated);
            remaining -= valueLength;
            truncated |= valueTruncated;
            captured.Add(new KeyValuePair<string, object?>(key, value));
        }

        if (truncated)
        {
            captured.Add(new KeyValuePair<string, object?>("workablePropertiesTruncated", true));
        }

        return captured;
    }

    private static object? CapturePropertyValue(
        object? value,
        int maximumLength,
        out int capturedLength,
        out bool truncated)
    {
        truncated = false;
        if (value is null)
        {
            capturedLength = 0;
            return null;
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal or Guid or DateTime or DateTimeOffset or TimeSpan)
        {
            capturedLength = Math.Min(value.ToString()?.Length ?? 0, maximumLength);
            return value;
        }

        var text = value is string stringValue ? stringValue : value.ToString() ?? string.Empty;
        capturedLength = Math.Min(text.Length, Math.Max(0, maximumLength));
        truncated = text.Length > capturedLength;
        return text[..capturedLength];
    }

    private bool IsProfileWithinBounds(WorkProfileSnapshot profile)
    {
        var nodes = 0;
        var pending = new Stack<WorkProfileSnapshotNode>();
        pending.Push(profile.Root);
        while (pending.TryPop(out var node))
        {
            if (++nodes > this.configuration.MaximumProfileNodeCount)
            {
                return false;
            }

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        try
        {
            using var stream = new BoundedWriteStream(this.configuration.MaximumProfileJsonLength);
            JsonSerializer.Serialize(stream, profile);
            return true;
        }
        catch (PayloadSizeLimitExceededException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            this.logger?.LogWarning(exception, "A persistent execution diagnostics profile could not be size checked.");
            return false;
        }
    }

    private static string Truncate(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? TruncateNullable(string? value, int maximumLength)
        => value is null ? null : Truncate(value, maximumLength);

    private static void AddOmittedInstrumentation(
        WorkProfileSnapshotNode node,
        Dictionary<string, InstrumentationAccumulator> accumulators)
    {
        if (!string.Equals(node.Label, "Automatic instrumentation truncated", StringComparison.Ordinal) ||
            node.Context is null)
        {
            return;
        }

        try
        {
            var context = node.Context is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(node.Context);
            if (!TryGetProperty(context, "OmittedByInstrumentation", out var omitted) ||
                omitted.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var entry in omitted.EnumerateObject())
            {
                if (!entry.Value.TryGetInt32(out var count) || count <= 0)
                {
                    continue;
                }

                if (!accumulators.TryGetValue(entry.Name, out var accumulator))
                {
                    accumulator = new InstrumentationAccumulator();
                    accumulators.Add(entry.Name, accumulator);
                }

                accumulator.OmittedNodeCount += count;
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Profile context is diagnostic evidence. A malformed or unsupported context must not fail the work.
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        var camelCaseName = JsonNamingPolicy.CamelCase.ConvertName(name);
        return element.TryGetProperty(camelCaseName, out value);
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private abstract record PersistenceOperation;

    private sealed record BeginOperation(WorkExecutionDiagnosticIterationStart Start) : PersistenceOperation;

    private sealed record LogOperation(
        CaptureState Capture,
        WorkExecutionDiagnosticLogRecord Record,
        IReadOnlyList<KeyValuePair<string, object?>> Properties) : PersistenceOperation
    {
        public long EstimatedBytes =>
            256L +
            (this.Record.Category.Length + this.Record.Message.Length +
             (this.Record.EventId.Name?.Length ?? 0) +
             (this.Record.ExceptionType?.Length ?? 0) +
             (this.Record.ExceptionMessage?.Length ?? 0) +
             (this.Record.ExceptionStackTrace?.Length ?? 0) +
             this.Properties.Sum(property =>
                 property.Key.Length + (property.Value?.ToString()?.Length ?? 0))) * sizeof(char);

        public WorkExecutionDiagnosticLogRecord Materialize()
        {
            string? propertiesJson = null;
            if (this.Properties.Count != 0)
            {
                try
                {
                    propertiesJson = JsonSerializer.Serialize(
                        this.Properties.ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal));
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
                {
                    propertiesJson = JsonSerializer.Serialize(new { workablePropertiesSerializationFailed = true });
                }
            }

            return this.Record with { PropertiesJson = propertiesJson };
        }
    }

    private sealed record ProfileOperation(
        CaptureState State,
        WorkerRecord Worker,
        long IterationSequence,
        WorkProfile Profile,
        bool PreserveWorkerProfile) : PersistenceOperation;

    private sealed record CompleteOperation(
        WorkerIterationReference Reference,
        CaptureState State,
        IterationCompletion Completion) : PersistenceOperation;

    private sealed record IterationCompletion(
        WorkCompletionStatus Status,
        int AttemptCount,
        DateTimeOffset CompletedAt,
        TimeSpan ExecutionDuration,
        WorkProfileSnapshot? Profile);

    private sealed record FlushOperation(TaskCompletionSource Completion) : PersistenceOperation;

    private sealed class CaptureState(Guid diagnosticId, WorkExecutionDiagnosticsPolicy policy)
    {
        private long ordinal;
        private long persisted;
        private long dropped;
        private long accepted;
        private long acceptedBytes;
        private readonly Lock logBudgetSync = new();
        private WorkProfileSnapshot? profile;
        private int profileExpected;
        private int profileDropped;
        private int completionStarted;

        public Guid DiagnosticId { get; } = diagnosticId;

        public WorkExecutionDiagnosticsPolicy Policy { get; } = policy;

        public long PersistedLogCount => Volatile.Read(ref this.persisted);

        public long DroppedLogCount => Volatile.Read(ref this.dropped);

        public WorkProfileSnapshot? Profile => Volatile.Read(ref this.profile);

        public bool ProfileDropped => Volatile.Read(ref this.profileDropped) != 0;

        public bool ProfileExpected => Volatile.Read(ref this.profileExpected) != 0;

        public bool TryBeginCompletion()
            => Interlocked.CompareExchange(ref this.completionStarted, 1, 0) == 0;

        public long NextOrdinal() => Interlocked.Increment(ref this.ordinal) - 1;

        public bool TryAcceptLog(int maximumCount, long maximumBytes, long estimatedBytes)
        {
            lock (this.logBudgetSync)
            {
                if (this.accepted >= maximumCount || this.acceptedBytes + estimatedBytes > maximumBytes)
                {
                    Interlocked.Increment(ref this.dropped);
                    return false;
                }

                this.accepted++;
                this.acceptedBytes += estimatedBytes;
                return true;
            }
        }

        public bool MayAcceptLog(int maximumCount, long maximumBytes)
        {
            lock (this.logBudgetSync)
            {
                if (this.accepted < maximumCount && this.acceptedBytes < maximumBytes)
                {
                    return true;
                }
            }

            Interlocked.Increment(ref this.dropped);
            return false;
        }

        public void RecordPersisted() => Interlocked.Increment(ref this.persisted);

        public void RecordDrop() => Interlocked.Increment(ref this.dropped);

        public void RecordQueueDrop(long estimatedBytes)
        {
            lock (this.logBudgetSync)
            {
                this.accepted--;
                this.acceptedBytes -= estimatedBytes;
            }

            Interlocked.Increment(ref this.dropped);
        }

        public void RecordMaterializationDrop()
        {
            Interlocked.Decrement(ref this.persisted);
            Interlocked.Increment(ref this.dropped);
        }

        public void MarkProfileExpected() => Volatile.Write(ref this.profileExpected, 1);

        public void MarkProfileDropped()
        {
            if (Volatile.Read(ref this.profileExpected) != 0)
            {
                Volatile.Write(ref this.profileDropped, 1);
            }
        }

        public void SetProfile(WorkProfileSnapshot capturedProfile)
        {
            Volatile.Write(ref this.profile, capturedProfile);
            Volatile.Write(ref this.profileDropped, 0);
        }

    }

    private sealed class InstrumentationAccumulator
    {
        public int NodeCount { get; set; }

        public int TimingCount { get; set; }

        public long TotalTimingMilliseconds { get; set; }

        public long MaximumTimingMilliseconds { get; set; }

        public int OmittedNodeCount { get; set; }
    }

    private sealed class CaptureRuleIndex
    {
        private readonly IReadOnlyList<WorkExecutionDiagnosticCaptureRule> all;
        private readonly IReadOnlyDictionary<string, WorkExecutionDiagnosticCaptureRule[]> byDefinition;
        private readonly WorkExecutionDiagnosticCaptureRule[] systemRules;
        private readonly ConcurrentDictionary<string, CachedRuleResolution> resolved =
            new(StringComparer.OrdinalIgnoreCase);

        private CaptureRuleIndex(
            IReadOnlyList<WorkExecutionDiagnosticCaptureRule> all,
            IReadOnlyDictionary<string, WorkExecutionDiagnosticCaptureRule[]> byDefinition,
            WorkExecutionDiagnosticCaptureRule[] systemRules)
        {
            this.all = all;
            this.byDefinition = byDefinition;
            this.systemRules = systemRules;
        }

        public static CaptureRuleIndex Empty { get; } = Create([]);

        public static CaptureRuleIndex Create(IEnumerable<WorkExecutionDiagnosticCaptureRule> rules)
        {
            var all = rules.OrderByDescending(rule => rule.CreatedAt).ThenByDescending(rule => rule.Id).ToArray();
            var byDefinition = all
                .Where(rule => rule.DefinitionName is not null)
                .GroupBy(rule => rule.DefinitionName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            return new CaptureRuleIndex(
                all,
                byDefinition,
                all.Where(rule => rule.DefinitionName is null).ToArray());
        }

        public WorkExecutionDiagnosticCaptureRule? Resolve(string definitionName, DateTimeOffset now)
        {
            if (this.resolved.TryGetValue(definitionName, out var cached) && cached.ValidUntil > now)
            {
                return cached.Rule;
            }

            WorkExecutionDiagnosticCaptureRule? selected = null;
            if (this.byDefinition.TryGetValue(definitionName, out var exact))
            {
                selected = exact.FirstOrDefault(rule => rule.ActiveUntil > now);
            }

            selected ??= this.systemRules.FirstOrDefault(rule => rule.ActiveUntil > now);
            this.resolved[definitionName] = new CachedRuleResolution(
                selected,
                selected?.ActiveUntil ?? DateTimeOffset.MaxValue);
            return selected;
        }

        public IReadOnlyList<WorkExecutionDiagnosticCaptureRule> GetActive(DateTimeOffset now)
            => [.. this.all.Where(rule => rule.ActiveUntil > now)];

        public CaptureRuleIndex WithUpsert(WorkExecutionDiagnosticCaptureRule rule)
            => Create(this.all.Where(existing => existing.Id != rule.Id).Append(rule));

        public CaptureRuleIndex WithDelete(Guid id)
            => Create(this.all.Where(rule => rule.Id != id));

        private sealed record CachedRuleResolution(
            WorkExecutionDiagnosticCaptureRule? Rule,
            DateTimeOffset ValidUntil);
    }

    private sealed class PayloadSizeLimitExceededException : Exception
    {
    }

    private sealed class BoundedWriteStream(long maximumBytes) : Stream
    {
        private long written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => this.written;

        public override long Position
        {
            get => this.written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => this.Advance(count);

        public override void Write(ReadOnlySpan<byte> buffer) => this.Advance(buffer.Length);

        private void Advance(int count)
        {
            if (this.written + count > maximumBytes)
            {
                throw new PayloadSizeLimitExceededException();
            }

            this.written += count;
        }
    }
}
