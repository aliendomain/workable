using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Workable;

internal sealed class WorkScheduler : IWorkScheduler, IDisposable
{
    private static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FallbackPollingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HostPresenceLeaseDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HostPresenceObservationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HostPresenceShutdownTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HostPresenceRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private const int MaximumCleanupBatchesPerInterval = 10;
    internal const int MaximumPersistedActorFieldLength = 512;
    private readonly string? systemName;
    private readonly WorkSystemCatalog catalog;
    private readonly Func<WorkSystemState> getSystemState;
    private readonly WorkQueueService queue;
    private readonly IWorkScheduleStore? store;
    private readonly WorkSystemSchedulingConfiguration configuration;
    private readonly ILogger logger;
    private readonly SemaphoreSlim signal = new(0);
    private readonly Lock lifecycleSync = new();
    private CancellationTokenSource? lifetime;
    private Task? backgroundTask;
    private DateTimeOffset startedAt;
    private Guid hostRunId;
    private DateTimeOffset nextCleanupAt;
    private DateTimeOffset nextHostObservationAt;
    private bool signalPending;
    private bool initialized;

    internal WorkScheduler(
        string? systemName,
        WorkSystemCatalog catalog,
        Func<WorkSystemState> getSystemState,
        WorkQueueService queue,
        IWorkScheduleStore? store,
        WorkSystemSchedulingConfiguration configuration,
        ILogger? logger = null)
    {
        this.systemName = systemName;
        this.catalog = catalog;
        this.getSystemState = getSystemState;
        this.queue = queue;
        this.store = store;
        this.configuration = configuration;
        this.logger = logger ?? NullLogger.Instance;
    }

    internal bool IsAvailable => this.configuration.IsEnabled && this.store is not null && this.initialized;

    internal async Task Initialize(CancellationToken cancellationToken)
    {
        if (!this.configuration.IsEnabled)
        {
            return;
        }

        if (this.store is null)
        {
            throw new InvalidOperationException(
                $"Runtime scheduling is enabled for work system '{FormatSystemName(this.systemName)}', but no IWorkScheduleStore is registered.");
        }

        await this.store.Initialize(new WorkScheduleStoreInitializationContext(this.systemName), cancellationToken);
        this.initialized = true;
    }

    internal void Start()
    {
        if (!this.IsAvailable)
        {
            return;
        }

        lock (this.lifecycleSync)
        {
            if (this.backgroundTask is { IsCompleted: false })
            {
                return;
            }

            this.lifetime?.Dispose();
            this.lifetime = new CancellationTokenSource();
            this.startedAt = DateTimeOffset.UtcNow;
            this.hostRunId = Guid.NewGuid();
            this.nextCleanupAt = this.startedAt;
            this.nextHostObservationAt = this.startedAt;
            using (ExecutionContext.SuppressFlow())
            {
                this.backgroundTask = Task.Run(() => this.Run(this.lifetime.Token), CancellationToken.None);
            }
        }

        this.Signal();
    }

    internal async Task Stop(CancellationToken cancellationToken)
    {
        Task? task;
        lock (this.lifecycleSync)
        {
            this.lifetime?.Cancel();
            task = this.backgroundTask;
        }

        this.Signal();
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken);
        }

        if (this.store is not null && this.hostRunId != Guid.Empty)
        {
            using var hostEndCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hostEndCancellation.CancelAfter(HostPresenceShutdownTimeout);
            try
            {
                await this.store.EndHost(
                    new WorkScheduleHostEnd(this.systemName, this.hostRunId, DateTimeOffset.UtcNow),
                    hostEndCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (hostEndCancellation.IsCancellationRequested)
            {
                this.logger.LogWarning(
                    "Scheduler host presence cleanup timed out for system {WorkSystemName}.",
                    FormatSystemName(this.systemName));
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                this.logger.LogWarning(
                    exception,
                    "Scheduler host presence could not be ended for system {WorkSystemName}.",
                    FormatSystemName(this.systemName));
            }
        }
    }

    public Task<WorkScheduleCreationOutcome> Create(
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.Create(
            request,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);
    }

    public Task<WorkScheduleCreationOutcome> Create<TInput>(
        string definitionName,
        TInput input,
        WorkScheduleTiming timing,
        WorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default)
        => this.Create(
            new WorkScheduleRequest(
                definitionName,
                timing,
                input is null ? null : WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
                workerOptions),
            cancellationToken);

    internal Task<WorkScheduleCreationOutcome> Create(
        WorkScheduleRequest request,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var work = this.catalog.TryGetWork(request.DefinitionName, out var registeredWork)
            ? registeredWork
            : null;
        var executionGrant = work is null
            ? WorkScheduleExecutionGrant.Unrestricted
            : new WorkScheduleExecutionGrant(true, work.Definition.ScheduleSecurityVersion);
        return this.Create(
            request,
            requestContext,
            executionGrant,
            work,
            cancellationToken);
    }

    internal async Task<WorkScheduleCreationOutcome> Create(
        WorkScheduleRequest request,
        WorkRequestContext requestContext,
        WorkScheduleExecutionGrant executionGrant,
        RegisteredWork? authorizedWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestContext);

        var errors = this.Validate(request, requestContext.Channel, authorizedWork).ToList();
        errors.AddRange(ValidateActor(requestContext.Actor, "requestContext.actor"));
        if (errors.Count > 0)
        {
            return new(WorkScheduleCreationStatus.Invalid, null, errors);
        }

        if (!this.IsAvailable)
        {
            return UnavailableCreation();
        }

        if (this.getSystemState() != WorkSystemState.Started)
        {
            return new(
                WorkScheduleCreationStatus.Invalid,
                null,
                [WorkMessage.Error(
                    "workable.schedule.system_not_started",
                    "The work system must be started before a schedule can be created.",
                    "system.state")]);
        }

        var now = DateTimeOffset.UtcNow;
        var nextRunAt = WorkScheduleTimingCalculator.GetInitialRunAt(request.Timing);
        var definitionName = authorizedWork?.Definition.Name ?? request.DefinitionName;
        var storedRequestContext = SnapshotRequestContext(requestContext).WithoutAuthorization();
        var snapshot = new WorkScheduleSnapshot(
            WorkScheduleId.New(),
            this.systemName,
            definitionName,
            request.Timing,
            SnapshotInput(request.Input),
            request.WorkerOptions,
            WorkScheduleStatus.Active,
            now,
            storedRequestContext.Actor,
            nextRunAt,
            LastRunAt: null,
            CanceledAt: null,
            CanceledBy: null);
        var persistenceRecord = new WorkSchedulePersistenceRecord(
            snapshot,
            storedRequestContext,
            executionGrant);
        var payloadSizeBytes = JsonSerializer.SerializeToUtf8Bytes(
            persistenceRecord,
            WorkData.DefaultJsonOptions).LongLength +
            WorkScheduleStoreCreateRequest.CancellationActorPayloadReserveBytes;
        var storeStatus = await this.store!.Create(
            new WorkScheduleStoreCreateRequest(
                persistenceRecord,
                payloadSizeBytes,
                this.configuration.MaximumSchedulePayloadBytes,
                this.configuration.MaximumActiveSchedules,
                this.configuration.MaximumActiveSchedulesPerDefinition,
                this.configuration.MaximumActiveSchedulesPerActor,
                this.configuration.MaximumRetainedSchedules,
                this.configuration.MaximumRetainedSchedulesPerActor,
                this.configuration.MaximumRetainedPayloadBytes,
                this.configuration.MaximumRetainedPayloadBytesPerActor,
                WorkScheduleStoreCreateRequest.CancellationActorPayloadReserveBytes),
            cancellationToken);
        if (storeStatus != WorkScheduleStoreCreationStatus.Accepted)
        {
            return RejectedCreation(storeStatus, definitionName);
        }

        this.Signal();
        return new(WorkScheduleCreationStatus.Accepted, snapshot, []);
    }

    public async Task<WorkScheduleSnapshot?> Get(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => (await this.GetRecord(scheduleId, cancellationToken))?.Schedule;

    internal Task<WorkSchedulePersistenceRecord?> GetRecord(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken)
        => this.store is null || !this.IsAvailable
            ? Task.FromResult<WorkSchedulePersistenceRecord?>(null)
            : this.store.Get(new WorkScheduleStoreReadRequest(this.systemName, scheduleId), cancellationToken);

    public Task<WorkScheduleQueryResult> List(
        WorkScheduleCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => this.List(criteria, definitionNames: null, cancellationToken);

    internal async Task<WorkScheduleQueryResult> List(
        WorkScheduleCriteria? criteria,
        IReadOnlySet<string>? definitionNames,
        CancellationToken cancellationToken)
    {
        criteria ??= new WorkScheduleCriteria();
        ValidateTake(criteria.Take, nameof(criteria));
        if (!this.IsAvailable)
        {
            return new([]);
        }

        var schedules = await this.store!.List(
            new WorkScheduleStoreListRequest(
                this.systemName,
                criteria.DefinitionName,
                criteria.Status,
                criteria.Take + 1,
                definitionNames,
                criteria.Cursor),
            cancellationToken);
        if (schedules.Count <= criteria.Take)
        {
            return new(schedules);
        }

        var page = schedules.Take(criteria.Take).ToArray();
        var last = page[^1];
        return new(page, new WorkScheduleCursor(last.CreatedAt, last.Id));
    }

    public Task<WorkScheduleUpcomingQueryResult> ListUpcoming(
        int take = 100,
        WorkScheduleUpcomingCursor? cursor = null,
        CancellationToken cancellationToken = default)
        => this.ListUpcoming(take, cursor, definitionNames: null, cancellationToken);

    internal async Task<WorkScheduleUpcomingQueryResult> ListUpcoming(
        int take,
        WorkScheduleUpcomingCursor? cursor,
        IReadOnlySet<string>? definitionNames,
        CancellationToken cancellationToken)
    {
        ValidateTake(take, nameof(take));
        if (!this.IsAvailable)
        {
            return new([], null, 0, 0, 0);
        }

        var result = await this.store!.ListUpcoming(
            new(this.systemName, take + 1, cursor, definitionNames),
            cancellationToken);
        if (result.Schedules.Count <= take)
        {
            return new(
                result.Schedules,
                null,
                result.ActiveScheduleCount,
                result.UpcomingScheduleCount,
                result.RecurringScheduleCount);
        }

        var page = result.Schedules.Take(take).ToArray();
        var last = page[^1];
        return new(
            page,
            new(last.NextRunAt!.Value, last.Id),
            result.ActiveScheduleCount,
            result.UpcomingScheduleCount,
            result.RecurringScheduleCount);
    }

    public Task<WorkScheduleOverviewResult> GetOverview(
        WorkScheduleOverviewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
        => this.GetOverview(criteria, definitionNames: null, cancellationToken);

    internal async Task<WorkScheduleOverviewResult> GetOverview(
        WorkScheduleOverviewCriteria? criteria,
        IReadOnlySet<string>? definitionNames,
        CancellationToken cancellationToken)
    {
        criteria ??= new WorkScheduleOverviewCriteria();
        ValidateTake(criteria.RecentScheduleTake, nameof(criteria.RecentScheduleTake));
        ValidateTake(criteria.UpcomingScheduleTake, nameof(criteria.UpcomingScheduleTake));
        ValidateTake(
            criteria.OccurrenceTake,
            nameof(criteria.OccurrenceTake),
            WorkScheduleOccurrenceReadRequest.MaximumTake);
        if (!this.IsAvailable)
        {
            return new(new([]), new([], null, 0, 0, 0), null, []);
        }

        var result = await this.store!.GetOverview(
            new(
                this.systemName,
                criteria.SelectedScheduleId,
                criteria.RecentScheduleTake,
                criteria.UpcomingScheduleTake,
                criteria.OccurrenceTake,
                this.configuration.MaximumOccurrenceQueryPayloadBytes,
                definitionNames,
                criteria.RecentCursor,
                criteria.UpcomingCursor),
            cancellationToken);
        var selected = result.SelectedSchedule;
        var occurrences = selected is null
            ? []
            : result.Occurrences
                .Where(occurrence => occurrence.ScheduleId == selected.Id)
                .Take(criteria.OccurrenceTake)
                .ToArray();
        return new(
            result.Recent with
            {
                Schedules = result.Recent.Schedules.Take(criteria.RecentScheduleTake).ToArray(),
            },
            result.Upcoming with
            {
                Schedules = result.Upcoming.Schedules.Take(criteria.UpcomingScheduleTake).ToArray(),
            },
            selected,
            occurrences);
    }

    public async Task<WorkScheduleOccurrenceQueryResult> ListOccurrences(
        WorkScheduleId scheduleId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ValidateTake(take, nameof(take), WorkScheduleOccurrenceReadRequest.MaximumTake);
        if (!this.IsAvailable)
        {
            return new([]);
        }

        return new(await this.store!.ListOccurrences(
            new WorkScheduleOccurrenceReadRequest(
                this.systemName,
                scheduleId,
                take,
                this.configuration.MaximumOccurrenceQueryPayloadBytes),
            cancellationToken));
    }

    public Task<WorkScheduleCancellationOutcome> Cancel(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default)
        => this.Cancel(
            scheduleId,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            cancellationToken);

    internal async Task<WorkScheduleCancellationOutcome> Cancel(
        WorkScheduleId scheduleId,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        if (!this.IsAvailable)
        {
            return new(
                WorkScheduleCancellationStatus.Unavailable,
                scheduleId,
                null,
                [UnavailableMessage()]);
        }

        var actorErrors = ValidateActor(requestContext.Actor, "requestContext.actor");
        if (actorErrors.Count > 0)
        {
            return new(
                WorkScheduleCancellationStatus.Invalid,
                scheduleId,
                null,
                actorErrors);
        }

        var canceledBy = SnapshotActor(requestContext.Actor);
        var canceled = await this.store!.Cancel(
            new WorkScheduleStoreCancelRequest(
                this.systemName,
                scheduleId,
                DateTimeOffset.UtcNow,
                canceledBy),
            cancellationToken);
        if (canceled is not null)
        {
            return new(WorkScheduleCancellationStatus.Accepted, scheduleId, canceled.Schedule, []);
        }

        var current = await this.GetRecord(scheduleId, cancellationToken);
        return current is null
            ? new(
                WorkScheduleCancellationStatus.NotFound,
                scheduleId,
                null,
                [WorkMessage.Error(
                    "workable.schedule.not_found",
                    $"No work schedule was found for '{scheduleId}'.",
                    "schedule")])
            : current.Schedule.Status == WorkScheduleStatus.Active
                ? new(
                    WorkScheduleCancellationStatus.Conflict,
                    scheduleId,
                    current.Schedule,
                    [WorkMessage.Error(
                        "workable.schedule.dispatch_in_progress",
                        $"Schedule '{scheduleId}' has begun dispatch and can no longer be canceled for this occurrence.",
                        "schedule.status")])
                : new(
                WorkScheduleCancellationStatus.Conflict,
                scheduleId,
                current.Schedule,
                [WorkMessage.Error(
                    "workable.schedule.not_active",
                    $"Schedule '{scheduleId}' is not active.",
                    "schedule.status")]);
    }

    public void Dispose()
    {
        lock (this.lifecycleSync)
        {
            this.lifetime?.Cancel();
            this.lifetime?.Dispose();
        }

        this.Signal();
        this.signal.Dispose();
    }

    private IReadOnlyList<WorkMessage> Validate(
        WorkScheduleRequest request,
        WorkInvocationChannel invocationChannel,
        RegisteredWork? work)
    {
        var messages = new List<WorkMessage>();
        if (string.IsNullOrWhiteSpace(request.DefinitionName))
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.definition_required",
                "A work definition name is required.",
                "definition"));
            return messages;
        }

        if (request.Timing is null)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.timing_required",
                "Schedule timing is required.",
                "timing"));
            return messages;
        }

        if (request.Timing.Interval is { } interval && interval < this.configuration.MinimumInterval)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.interval_invalid",
                "A recurring schedule interval must be at least one minute.",
                "timing.interval"));
        }

        if (request.Timing.Interval is not null && request.Timing.CronExpression is not null)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.recurrence_ambiguous",
                "A schedule cannot use both an interval and a cron expression.",
                "timing"));
        }

        if (request.Timing.CronExpression is not null)
        {
            if (request.Timing.CronExpression.Length > WorkScheduleTiming.MaximumCronExpressionLength)
            {
                messages.Add(WorkMessage.Error(
                    "workable.schedule.cron_too_long",
                    $"A cron expression cannot exceed {WorkScheduleTiming.MaximumCronExpressionLength} characters.",
                    "timing.cronExpression"));
            }
            else if (request.Timing.TimeZoneId?.Length > WorkScheduleTiming.MaximumTimeZoneIdLength)
            {
                messages.Add(WorkMessage.Error(
                    "workable.schedule.time_zone_too_long",
                    $"A time-zone id cannot exceed {WorkScheduleTiming.MaximumTimeZoneIdLength} characters.",
                    "timing.timeZoneId"));
            }
            else if (!WorkScheduleTimingCalculator.TryResolveCron(
                    request.Timing,
                    out _,
                    out _,
                    out var cronError))
            {
                messages.Add(WorkMessage.Error(
                    "workable.schedule.cron_invalid",
                    cronError!,
                    string.IsNullOrWhiteSpace(request.Timing.CronExpression)
                        ? "timing.cronExpression"
                        : string.IsNullOrWhiteSpace(request.Timing.TimeZoneId)
                            ? "timing.timeZoneId"
                            : "timing.cronExpression"));
            }
            else if (WorkScheduleTimingCalculator.GetInitialRunAt(request.Timing) is null)
            {
                messages.Add(WorkMessage.Error(
                    "workable.schedule.cron_no_occurrence",
                    "The cron expression has no occurrence on or after the schedule start.",
                    "timing.cronExpression"));
            }
        }
        else if (request.Timing.TimeZoneId is not null)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.time_zone_without_cron",
                "A time zone can only be specified for a cron schedule.",
                "timing.timeZoneId"));
        }

        if (request.WorkerOptions?.QueueDurabilityTransaction is not null)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.transaction_not_supported",
                "A caller-owned durability transaction cannot be retained by a schedule.",
                "workerOptions.queueDurabilityTransaction"));
        }

        if (work is null)
        {
            messages.Add(WorkMessage.Error(
                "workable.definition.not_found",
                $"No work definition was found for '{request.DefinitionName}'.",
                "definition"));
            return messages;
        }

        if (request.Timing.IsRecurring && work.DefaultRuntimePlan.Configuration.Recurrence.IsEnabled)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.static_recurrence_conflict",
                $"Work '{request.DefinitionName}' already has statically configured recurrence and cannot receive a recurring runtime schedule.",
                request.Timing.IsCron ? "timing.cronExpression" : "timing.interval"));
        }

        var queueValidation = this.queue.ValidateScheduled(
            work,
            request.Input,
            NormalizeWorkerOptionsForScheduledDispatch(
                work,
                request.Timing,
                request.WorkerOptions),
            WorkRequestContext.Create(invocationChannel));
        if (queueValidation is not null)
        {
            messages.AddRange(queueValidation.Messages);
        }

        return messages;
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var observedAt = DateTimeOffset.UtcNow;
                var claimRequest = new WorkScheduleClaimRequest(
                    this.systemName,
                    observedAt,
                    ClaimLeaseDuration,
                    this.configuration.MaximumDispatchesPerBatch,
                    this.configuration.MaximumClaimedPayloadBytesPerBatch);
                IReadOnlyList<WorkScheduleClaim> claims;
                if (observedAt >= this.nextHostObservationAt)
                {
                    claims = await this.store!.ClaimDueAndObserveHost(
                        claimRequest,
                        new WorkScheduleHostObservation(
                            this.systemName,
                            this.hostRunId,
                            this.startedAt,
                            observedAt,
                            observedAt + HostPresenceLeaseDuration,
                            observedAt - HostPresenceRetention),
                        cancellationToken);
                    this.nextHostObservationAt = observedAt + HostPresenceObservationInterval;
                }
                else
                {
                    claims = await this.store!.ClaimDue(claimRequest, cancellationToken);
                }
                var availableTimes = await this.FindAvailableTimes(claims, cancellationToken);
                await Task.WhenAll(claims.Select(claim => this.Dispatch(
                    claim,
                    availableTimes.Contains(claim.ScheduledAt),
                    cancellationToken)));

                await this.CleanupIfDue(cancellationToken);
                if (claims.Count == 0)
                {
                    await this.WaitForSignal(FallbackPollingInterval, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                this.logger.LogError(exception, "Runtime work scheduling failed for system {WorkSystemName}.", FormatSystemName(this.systemName));
                try
                {
                    await this.WaitForSignal(FallbackPollingInterval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task Dispatch(
        WorkScheduleClaim claim,
        bool systemWasAvailableWhenDue,
        CancellationToken cancellationToken)
    {
        var record = claim.Record;
        var dispatchStartedAt = DateTimeOffset.UtcNow;
        if (!await this.store!.BeginDispatch(
                new WorkScheduleDispatchStart(
                    this.systemName,
                    record.Schedule.Id,
                    claim.LeaseId,
                    dispatchStartedAt,
                    dispatchStartedAt + ClaimLeaseDuration),
                cancellationToken))
        {
            return;
        }

        var attemptedAt = DateTimeOffset.UtcNow;
        var skipMissed = !record.Schedule.Timing.RunMissedExecution &&
            record.Schedule.CreatedAt < this.startedAt &&
            claim.ScheduledAt < this.startedAt &&
            !systemWasAvailableWhenDue;
        WorkScheduleOccurrence occurrence;
        if (skipMissed)
        {
            occurrence = CreateOccurrence(
                claim,
                attemptedAt,
                WorkScheduleOccurrenceStatus.Skipped,
                queueOutcome: null,
                [WorkMessage.Information(
                    "workable.schedule.missed_skipped",
                    "The occurrence was skipped because the work system was down and retrying missed executions is disabled.")]);
        }
        else
        {
            try
            {
                var handle = await this.queue.EnqueueScheduled(
                    record.Schedule.DefinitionName,
                    record.Schedule.Input,
                    record.Schedule.Timing,
                    record.Schedule.WorkerOptions,
                    record.RequestContext.WithoutAuthorization(),
                    record.ExecutionGrant,
                    cancellationToken);
                occurrence = CreateOccurrence(
                    claim,
                    attemptedAt,
                    handle.QueueOutcome.IsAccepted
                        ? WorkScheduleOccurrenceStatus.Accepted
                        : WorkScheduleOccurrenceStatus.Rejected,
                    handle.QueueOutcome,
                    handle.QueueOutcome.Messages);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                occurrence = CreateOccurrence(
                    claim,
                    attemptedAt,
                    WorkScheduleOccurrenceStatus.Failed,
                    queueOutcome: null,
                    [WorkMessage.Error(
                        "workable.schedule.dispatch_failed",
                        $"The scheduled occurrence could not be dispatched ({exception.GetType().Name}).",
                        "schedule.dispatch")]);
            }
        }

        var nextRunAt = WorkScheduleTimingCalculator.GetNextRunAt(
            record.Schedule.Timing,
            claim.ScheduledAt,
            attemptedAt);
        var status = nextRunAt is null ? WorkScheduleStatus.Completed : WorkScheduleStatus.Active;
        occurrence = BoundOccurrenceMessages(occurrence, this.configuration.MaximumOccurrencePayloadBytes);
        await this.store!.CompleteClaim(
            new WorkScheduleClaimCompletion(
                this.systemName,
                record.Schedule.Id,
                claim.LeaseId,
                occurrence,
                status,
                nextRunAt,
                this.configuration.MaximumRetainedOccurrences,
                this.configuration.MaximumRetainedOccurrencePayloadBytes),
            cancellationToken);
    }

    private async Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
        IReadOnlyList<WorkScheduleClaim> claims,
        CancellationToken cancellationToken)
    {
        var scheduledTimes = claims
            .Where(claim => !claim.Record.Schedule.Timing.RunMissedExecution &&
                claim.Record.Schedule.CreatedAt < this.startedAt &&
                claim.ScheduledAt < this.startedAt)
            .Select(static claim => claim.ScheduledAt)
            .ToHashSet();
        return scheduledTimes.Count == 0
            ? scheduledTimes
            : await this.store!.FindAvailableTimes(
                new WorkScheduleHostAvailabilityRequest(this.systemName, scheduledTimes),
                cancellationToken);
    }

    internal static WorkerOptions NormalizeWorkerOptionsForScheduledDispatch(
        RegisteredWork currentWork,
        WorkScheduleTiming? timing,
        WorkerOptions? workerOptions)
    {
        var configuration = workerOptions?.Configuration;
        if (configuration is null)
        {
            configuration = currentWork.DefaultRuntimePlan.Configuration;
        }

        configuration ??= WorkConfiguration.Default;
        configuration = configuration with
        {
            Start = configuration.Start with { Policy = WorkStartPolicy.StartAndReturnAfterAccepted },
            Recurrence = timing?.IsRecurring == true
                ? WorkRecurrenceConfiguration.Disabled
                : configuration.Recurrence,
        };
        return (workerOptions ?? new WorkerOptions()) with { Configuration = configuration };
    }

    private WorkScheduleOccurrence CreateOccurrence(
        WorkScheduleClaim claim,
        DateTimeOffset attemptedAt,
        WorkScheduleOccurrenceStatus status,
        WorkQueueOutcome? queueOutcome,
        IReadOnlyList<WorkMessage> messages)
        => new(
            Guid.NewGuid(),
            claim.Record.Schedule.Id,
            claim.ScheduledAt,
            attemptedAt,
            status,
            queueOutcome?.Status,
            queueOutcome?.WorkerId,
            messages,
            attemptedAt + this.configuration.HistoryRetention);

    internal static DateTimeOffset? GetNextRunAt(
        TimeSpan? interval,
        DateTimeOffset scheduledAt,
        DateTimeOffset now)
        => WorkScheduleTimingCalculator.GetNextRunAt(
            new WorkScheduleTiming(scheduledAt, interval),
            scheduledAt,
            now);

    private async Task CleanupIfDue(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < this.nextCleanupAt)
        {
            return;
        }

        await this.DrainCleanupBatches(
            maximumCount => this.store!.DeleteExpiredOccurrences(
                new WorkScheduleOccurrenceExpirationRequest(this.systemName, now, maximumCount),
                cancellationToken));
        await this.DrainCleanupBatches(
            maximumCount => this.store!.DeleteExpiredSchedules(
                new WorkScheduleExpirationRequest(
                    this.systemName,
                    now - this.configuration.HistoryRetention,
                    maximumCount),
                cancellationToken));
        this.nextCleanupAt = now + CleanupInterval;
    }

    private async Task DrainCleanupBatches(Func<int, Task<int>> delete)
    {
        const int batchSize = 1_000;
        for (var batch = 0; batch < MaximumCleanupBatchesPerInterval; batch++)
        {
            if (await delete(batchSize) < batchSize)
            {
                return;
            }
        }
    }

    private async Task WaitForSignal(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (await this.signal.WaitAsync(delay, cancellationToken))
        {
            lock (this.lifecycleSync)
            {
                this.signalPending = false;
            }
        }
    }

    private void Signal()
    {
        var release = false;
        lock (this.lifecycleSync)
        {
            if (!this.signalPending)
            {
                this.signalPending = true;
                release = true;
            }
        }

        if (release)
        {
            this.signal.Release();
        }
    }

    private static WorkInput? SnapshotInput(WorkInput? input)
        => input is null
            ? null
            : input with { Identifiers = input.Identifiers?.ToHashSet() };

    private static WorkRequestContext SnapshotRequestContext(WorkRequestContext requestContext)
        => requestContext with
        {
            Origin = requestContext.Origin with { Actor = SnapshotActor(requestContext.Actor) },
        };

    private static WorkActor SnapshotActor(WorkActor actor)
        => new(actor.Id, actor.Name, actor.Email);

    private static List<WorkMessage> ValidateActor(WorkActor actor, string target)
    {
        var messages = new List<WorkMessage>();
        ValidateActorField(actor.Id, "id", messages, target);
        ValidateActorField(actor.Name, "name", messages, target);
        ValidateActorField(actor.Email, "email", messages, target);
        return messages;
    }

    private static void ValidateActorField(
        string? value,
        string field,
        List<WorkMessage> messages,
        string target)
    {
        if (value?.Length > MaximumPersistedActorFieldLength)
        {
            messages.Add(WorkMessage.Error(
                "workable.schedule.actor_field_too_long",
                $"The schedule actor {field} cannot exceed {MaximumPersistedActorFieldLength} characters.",
                $"{target}.{field}"));
        }
    }

    private static WorkScheduleOccurrence BoundOccurrenceMessages(
        WorkScheduleOccurrence occurrence,
        long maximumPayloadBytes)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            occurrence.Messages,
            WorkData.DefaultJsonOptions).LongLength;
        if (payloadBytes <= maximumPayloadBytes)
        {
            return occurrence;
        }

        IReadOnlyList<WorkMessage> replacement =
        [
            WorkMessage.Warning(
                "workable.schedule.occurrence_messages_omitted",
                "Dispatch messages exceeded the configured schedule-occurrence payload limit and were omitted.",
                "schedule.occurrence.messages"),
        ];
        if (JsonSerializer.SerializeToUtf8Bytes(replacement, WorkData.DefaultJsonOptions).LongLength > maximumPayloadBytes)
        {
            replacement = [];
        }

        return occurrence with { Messages = replacement };
    }

    internal static void ValidateTake(
        int take,
        string parameterName,
        int maximumTake = WorkScheduleCriteria.MaximumTake)
    {
        if (take <= 0 || take > maximumTake)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                take,
                $"Schedule query take must be between 1 and {maximumTake}.");
        }
    }

    private static WorkScheduleCreationOutcome UnavailableCreation()
        => new(WorkScheduleCreationStatus.Unavailable, null, [UnavailableMessage()]);

    private static WorkScheduleCreationOutcome RejectedCreation(
        WorkScheduleStoreCreationStatus status,
        string definitionName)
        => status switch
        {
            WorkScheduleStoreCreationStatus.InvalidDefinitionName => new(
                WorkScheduleCreationStatus.Invalid,
                null,
                [WorkMessage.Error(
                    "workable.schedule.definition_name_too_long",
                    "The work definition name exceeds the schedule store's supported length.",
                    "definition")]),
            WorkScheduleStoreCreationStatus.PayloadTooLarge => new(
                WorkScheduleCreationStatus.Invalid,
                null,
                [WorkMessage.Error(
                    "workable.schedule.payload_too_large",
                    "The serialized schedule payload exceeds the configured size limit.",
                    "schedule.payload")]),
            WorkScheduleStoreCreationStatus.SystemLimitReached => new(
                WorkScheduleCreationStatus.LimitReached,
                null,
                [WorkMessage.Error(
                    "workable.schedule.system_limit_reached",
                    "The work system has reached its active schedule limit.",
                    "schedule.capacity")]),
            WorkScheduleStoreCreationStatus.DefinitionLimitReached => new(
                WorkScheduleCreationStatus.LimitReached,
                null,
                [WorkMessage.Error(
                    "workable.schedule.definition_limit_reached",
                    $"Work '{definitionName}' has reached its active schedule limit.",
                    "schedule.capacity")]),
            WorkScheduleStoreCreationStatus.ActiveActorLimitReached => new(
                WorkScheduleCreationStatus.LimitReached,
                null,
                [WorkMessage.Error(
                    "workable.schedule.active_actor_limit_reached",
                    "The schedule creator has reached their active schedule capacity.",
                    "schedule.capacity")]),
            WorkScheduleStoreCreationStatus.RetainedSystemLimitReached or
            WorkScheduleStoreCreationStatus.RetainedSystemBytesLimitReached => new(
                WorkScheduleCreationStatus.LimitReached,
                null,
                [WorkMessage.Error(
                    "workable.schedule.retained_system_limit_reached",
                    "The work system has reached its retained schedule capacity.",
                    "schedule.capacity")]),
            WorkScheduleStoreCreationStatus.RetainedActorLimitReached or
            WorkScheduleStoreCreationStatus.RetainedActorBytesLimitReached => new(
                WorkScheduleCreationStatus.LimitReached,
                null,
                [WorkMessage.Error(
                    "workable.schedule.retained_actor_limit_reached",
                    "The schedule creator has reached their retained schedule capacity.",
                    "schedule.capacity")]),
            _ => throw new InvalidOperationException($"Unsupported schedule-store creation status '{status}'."),
        };

    private static WorkMessage UnavailableMessage()
        => WorkMessage.Error(
            "workable.schedule.unavailable",
            "Runtime scheduling is not configured or its persistence store is unavailable.",
            "schedule");

    private static string FormatSystemName(string? name)
        => string.IsNullOrWhiteSpace(name) ? "default" : name;
}
