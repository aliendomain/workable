namespace Workable;

internal sealed class AuthorizedWorkScheduler(
    WorkSystemCatalog catalog,
    WorkScheduler inner,
    WorkAuthorizationEvaluator authorization,
    WorkRequestContext requestContext) : IWorkScheduler
{
    public async Task<WorkScheduleCreationOutcome> Create(
        WorkScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DefinitionName) ||
            !catalog.TryGetWork(request.DefinitionName, out var work) ||
            !authorization.CanDiscover(work.Definition))
        {
            return NotFoundCreation(request.DefinitionName);
        }

        var effectiveOptions = WorkScheduler.NormalizeWorkerOptionsForScheduledDispatch(
            work,
            request.Timing,
            request.WorkerOptions);
        var decision = authorization.AuthorizeQueue(
            work,
            request.Input,
            effectiveOptions,
            requestContext);
        if (!decision.IsAllowed)
        {
            return decision.IsInvalid
                ? new(WorkScheduleCreationStatus.Invalid, null, decision.Messages)
                : new(
                    WorkScheduleCreationStatus.Unauthorized,
                    null,
                    [WorkMessage.Error(
                        "workable.definition.unauthorized",
                        $"You are not authorized to schedule work '{request.DefinitionName}'.",
                        "definition.authorization")]);
        }

        return await inner.Create(
            request,
            requestContext,
            new WorkScheduleExecutionGrant(
                authorization.CanViewDiagnostics(),
                work.Definition.ScheduleSecurityVersion),
            work,
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

    public async Task<WorkScheduleSnapshot?> Get(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var record = await inner.GetRecord(scheduleId, cancellationToken);
        return record is not null && this.CanRead(record.Schedule.DefinitionName)
            ? record.Schedule
            : null;
    }

    public async Task<WorkScheduleQueryResult> List(
        WorkScheduleCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        criteria ??= new WorkScheduleCriteria();
        WorkScheduler.ValidateTake(criteria.Take, nameof(criteria));
        IReadOnlySet<string>? readableDefinitionNames = null;
        if (!authorization.HasSystemReadAllWorkAccess())
        {
            readableDefinitionNames = catalog.Definitions
                .Where(authorization.CanRead)
                .Select(static definition => definition.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (readableDefinitionNames.Count == 0)
            {
                return new([]);
            }
        }

        var result = await inner.List(
            criteria,
            readableDefinitionNames,
            cancellationToken);
        return new(
            [.. result.Schedules
                .Where(schedule => this.CanRead(schedule.DefinitionName))
                .Take(criteria.Take)],
            result.Cursor);
    }

    public async Task<WorkScheduleOccurrenceQueryResult> ListOccurrences(
        WorkScheduleId scheduleId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        WorkScheduler.ValidateTake(
            take,
            nameof(take),
            WorkScheduleOccurrenceReadRequest.MaximumTake);
        var schedule = await this.Get(scheduleId, cancellationToken);
        if (schedule is null)
        {
            return new([]);
        }

        return await inner.ListOccurrences(scheduleId, take, cancellationToken);
    }

    public async Task<WorkScheduleCancellationOutcome> Cancel(
        WorkScheduleId scheduleId,
        CancellationToken cancellationToken = default)
    {
        var record = await inner.GetRecord(scheduleId, cancellationToken);
        if (record is null)
        {
            return NotFoundCancellation(scheduleId);
        }

        if (!catalog.TryGetWork(record.Schedule.DefinitionName, out var work))
        {
            if (!authorization.HasSystemOperateAllWorkAccess())
            {
                return NotFoundCancellation(scheduleId);
            }

            var orphanedOutcome = await inner.Cancel(scheduleId, requestContext, cancellationToken);
            return authorization.HasSystemReadAllWorkAccess()
                ? orphanedOutcome
                : orphanedOutcome with { Schedule = null };
        }

        if (!authorization.CanDiscover(work.Definition))
        {
            return NotFoundCancellation(scheduleId);
        }

        var decision = authorization.AuthorizeScheduleCancel(
            work,
            record.Schedule.Id,
            record.Schedule.Input,
            requestContext);
        if (!decision.IsAllowed)
        {
            return decision.IsInvalid
                ? new(
                    WorkScheduleCancellationStatus.Invalid,
                    scheduleId,
                    null,
                    decision.Messages)
                : new(
                    WorkScheduleCancellationStatus.Unauthorized,
                    scheduleId,
                    null,
                    [WorkMessage.Error(
                        "workable.schedule.unauthorized",
                        $"You are not authorized to cancel schedule '{scheduleId}'.",
                        "schedule.authorization")]);
        }

        var outcome = await inner.Cancel(scheduleId, requestContext, cancellationToken);
        return this.CanRead(record.Schedule.DefinitionName)
            ? outcome
            : outcome with { Schedule = null };
    }

    private bool CanRead(string definitionName)
        => catalog.TryGet(definitionName, out var definition)
            ? authorization.CanRead(definition)
            : authorization.HasSystemReadAllWorkAccess();

    private static WorkScheduleCreationOutcome NotFoundCreation(string? definitionName)
        => new(
            WorkScheduleCreationStatus.NotFound,
            null,
            [WorkMessage.Error(
                "workable.definition.not_found",
                $"No work definition was found for '{definitionName}'.",
                "definition")]);

    private static WorkScheduleCancellationOutcome NotFoundCancellation(WorkScheduleId scheduleId)
        => new(
            WorkScheduleCancellationStatus.NotFound,
            scheduleId,
            null,
            [WorkMessage.Error(
                "workable.schedule.not_found",
                $"No work schedule was found for '{scheduleId}'.",
                "schedule")]);
}
