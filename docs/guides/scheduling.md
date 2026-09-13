# Runtime Scheduling

Runtime scheduling lets an authorized caller arrange for registered work to be queued later: once, at a fixed elapsed-time interval, or at calendar times described by a cron expression. Schedules belong to one Workable system and identify work by definition name because runtime work ids are regenerated when a system starts.

The complete schedule-management API is available in process. The built-in HTTP API and admin UI expose creation, schedule queries, retained dispatch history, and cancellation. MCP and SignalR do not currently expose schedule management.

## Configure Scheduling

Scheduling is opt-in and requires an `IWorkScheduleStore`. The SQL Server persistence package supplies the built-in durable implementation:

```csharp
services.AddWorkableSqlServerPersistence(
    connectionString,
    schemaName: "workable",
    persistenceScope: "my-application");

services.AddWorkableSystem(builder => builder
    .EnableScheduling(
        historyRetention: TimeSpan.FromDays(1),
        maximumActiveSchedules: 1_000,
        maximumActiveSchedulesPerDefinition: 100,
        maximumActiveSchedulesPerActor: 250)
    .AddWork<MyTestWork>(WorkDefinition.Create("tests.run")));
```

`HistoryRetention` controls retained schedule-dispatch occurrences and terminal schedule records, defaults to one day, and must be between one minute and seven days. Active schedules are retained until they complete or are canceled. Scheduling accepts at most 1,000 active schedules per work system, 100 per definition, and 250 per creator id by default.

`UseScheduling(...)` provides additional limits for retained schedules, serialized schedule data, occurrence history, dispatch batches, and occurrence queries. The defaults retain at most 10,000 schedules and 100,000 occurrences per system, with lower per-creator and payload-size limits. Completed and canceled schedules continue to count toward retained limits until `HistoryRetention` expires. When occurrence-history limits are reached, the oldest occurrences are removed first. All configured limits must be positive and large enough to accept at least one valid item:

```csharp
builder.UseScheduling(new WorkSystemSchedulingConfiguration
{
    IsEnabled = true,
    HistoryRetention = TimeSpan.FromHours(6),
    MaximumActiveSchedules = 1_000,
    MaximumActiveSchedulesPerDefinition = 100,
    MaximumActiveSchedulesPerActor = 250,
    MaximumRetainedSchedules = 10_000,
    MaximumRetainedSchedulesPerActor = 2_000,
    MaximumSchedulePayloadBytes = 1_048_576,
    MaximumRetainedPayloadBytes = 268_435_456,
    MaximumRetainedPayloadBytesPerActor = 67_108_864,
    MaximumRetainedOccurrences = 100_000,
    MaximumOccurrencePayloadBytes = 65_536,
    MaximumRetainedOccurrencePayloadBytes = 67_108_864,
    MaximumDispatchesPerBatch = 25,
    MaximumClaimedPayloadBytesPerBatch = 8_388_608,
    MaximumOccurrenceQueryPayloadBytes = 4_194_304,
});
```

Actual worker logs, profiles, iterations, and results follow the normal worker retention and [persistent execution diagnostics](configuration/execution-diagnostics-persistence.md) policies.

A host can instead register its own `IWorkScheduleStore`. The store contract includes scheduler-host presence operations so `RunMissedExecution` can distinguish logical-system downtime from one host joining an already healthy cluster. Enabling scheduling without a registered store fails system startup rather than silently accepting non-durable schedules.

## Create A Schedule

Use the scheduler on an `IWorkSystemSession` when the action belongs to a user or another authenticated caller:

```csharp
var session = await system.CreateSession(requestContext, cancellationToken);

var once = await session.Schedules.Create(
    new WorkScheduleRequest(
        "tests.run",
        WorkScheduleTiming.Once(
            DateTimeOffset.UtcNow.AddHours(1),
            runMissedExecution: true),
        WorkInput.FromValue(new { suite = "smoke" })),
    cancellationToken);

var recurring = await session.Schedules.Create(
    new WorkScheduleRequest(
        "tests.run",
        WorkScheduleTiming.Every(
            TimeSpan.FromMinutes(15),
            firstRunAt: DateTimeOffset.UtcNow.AddMinutes(5),
            runMissedExecution: false)),
    cancellationToken);

var weekdays = await session.Schedules.Create(
    new WorkScheduleRequest(
        "tests.run",
        WorkScheduleTiming.Cron(
            "0 9 * * 1-5",
            timeZoneId: "America/Los_Angeles",
            startsAt: DateTimeOffset.UtcNow,
            runMissedExecution: true)),
    cancellationToken);
```

The built-in HTTP adapter exposes `POST /workable/work/{definitionName}/schedules`. The admin UI provides the same capability through **Schedule** in the queue dialog and lets authorized users inspect upcoming and retained schedules, review dispatch history, and cancel active schedules.

Use intervals for elapsed-time requirements such as “every 90 minutes from the first run.” Interval schedules must recur no more frequently than once per minute. Use cron for calendar requirements such as “09:00 every weekday.” `FirstRunAt` is the actual first due instant for one-time and interval schedules. For cron schedules it is the earliest eligible instant; the first matching calendar occurrence on or after that instant becomes `NextRunAt`. An interval and cron expression are mutually exclusive.

Cron schedules use Cronos five-field syntax in the order `minute hour day-of-month month day-of-week`. Cron expressions and time-zone ids are each limited to 256 characters. When both day-of-month and day-of-week are restricted, both fields must match; this differs from cron dialects that treat those fields as alternatives. Workable evaluates the expression in `TimeZoneId` through the platform time-zone database and accounts for daylight-saving gaps and overlaps. Use stable IANA identifiers such as `America/Los_Angeles`, `Europe/London`, or `UTC` when schedules may run on different operating systems.

`Create<TInput>(...)` is also available for typed input. A schedule snapshots its input, queue-time worker overrides, and durable request context. Caller-owned SQL queue transactions cannot be attached because such a transaction cannot be retained until a future dispatch.

The target definition must exist when the schedule is created. The caller's invocation channel and the proposed input and worker options are dry-run through normal queue validation before anything is persisted. This rejects malformed or workflow-reserved identifiers, missing subject or concurrency-key inputs, invalid runtime configuration, and configurations whose persistence or diagnostics dependencies are unavailable. The checks run again against the latest definition when an occurrence dispatches. Each queue authorization decision is bound to that exact resolved definition for the rest of the immediate creation attempt.

## Authorization

Scheduling introduces no new permission category:

- Creating a schedule requires the target definition's `Queue` permission and queue authorization requirements. Requirement callbacks receive the effective scheduler options, including the forced background start policy and recurring-schedule recurrence suppression. Acceptance creates a durable execution grant for that schedule.
- Reading a schedule or its dispatch history requires `Read` permission for the target definition. If that definition has been removed, only an explicit system-level `ReadAllWork` grant exposes the orphaned schedule; having read access to every remaining definition is not sufficient.
- Canceling an active schedule requires the target definition's `Cancel` permission and schedule-action requirements. Use `WhenScheduleActionsRequire(...)` for a schedule-only rule or `WhenOperatingRequire(...)` for a rule shared with the other operation surfaces. A `Cancel` grant constrained only by `WhenWorkerActionsRequire(...)` fails closed for schedules instead of becoming an unconstrained schedule-cancellation grant. The canceler does not need to be the creator, and operate permission does not implicitly reveal the schedule snapshot.
- Hidden or removed definitions use the same non-disclosing not-found behavior as other authorized Workable surfaces.

Workable does not repeat creator authorization when an occurrence fires. The persisted request context retains actor, origin, description, URL, and authentication state for audit, but not an authorization snapshot. Dispatch uses a trusted scheduler path under the grant accepted at creation, so disabling or deleting the creator and changing their groups do not stop the schedule. Cancel the schedule explicitly to revoke that delegation; cancellation uses the acting caller's current permissions.

The durable grant cannot gain security-sensitive capabilities from later definition changes. In particular, Workable records whether the creator could use full profile capture. If the latest definition later enables full capture for a schedule created without that permission, dispatch is rejected instead of capturing unrestricted diagnostics. A creator who already had diagnostics permission keeps that granted capability even if their account or groups later change. Authorization, validation, and persistence at creation all use the same resolved definition instance, preventing a concurrent runtime reconfiguration from changing what is stored after the permission decision.

Each definition also has a developer-controlled `ScheduleSecurityVersion`, defaulting to `"1"`. Increment it in `WorkDefinition.Create(..., scheduleSecurityVersion: "2")` when a deployment changes the authority or business scope exercised by that work. Existing schedules retain the accepted version and reject dispatch if the latest definition does not match. This is explicit revocation at the definition-contract boundary; ordinary runtime reconfiguration continues to flow into scheduled execution without changing the version.

For an open system configured with `RequireAuthorization(false)`, schedule operations remain open just like direct queueing and worker operations.

## Latest Definition And Recurrence

Each occurrence resolves the work definition by name and uses its current definition and configuration. Reconfiguration between schedule creation and dispatch therefore affects the scheduled worker, and normal invocation-channel, input, runtime-configuration, capacity, persistence, and coordination validation still runs. Queue authorization requirements are the exception because their successful creation-time evaluation is the durable grant.

Runtime schedules and worker recurrence serve different purposes:

| Behavior | Worker recurrence | Runtime recurring schedule |
| --- | --- | --- |
| Created by | Static/default or queue-time worker configuration | A runtime caller |
| Identity | One worker across many iterations | A new worker for every occurrence |
| Wait state | The worker remains active in `Waiting` | The durable schedule remains active |
| Configuration | The worker's effective configuration | The latest definition plus retained schedule overrides |

A recurring runtime schedule cannot be created for work whose definition already enables recurrence. If recurrence is enabled on the definition later, runtime-scheduled recurring dispatches explicitly disable worker recurrence. This prevents one durable schedule occurrence from creating a second nested recurrence loop. A one-time schedule may still queue statically recurring work.

## Downtime And Failures

When `RunMissedExecution` is `true`, startup dispatches one overdue occurrence and advances a recurring schedule directly to its next future interval or cron occurrence. It does not emit a burst for every elapsed occurrence. When the option is `false`, an occurrence that became due while the logical work system was unavailable is recorded as `Skipped`, and the schedule advances in the same way. In multi-host deployments, Workable considers availability across the logical system rather than treating one host joining or leaving as system downtime.

Schedule occurrence status describes dispatch, not eventual execution:

- `Accepted` means the normal Workable queue accepted a worker.
- `Rejected` retains the queue outcome and safe messages.
- `Skipped` records the missed-run policy decision.
- `Failed` records an unexpected dispatch failure without persisting raw exception details.

After queue acceptance, the worker follows its current retry, failed-worker handling, auto-cancel, recurrence, logging, and retention policies. A recurring schedule remains independent of the result of an earlier worker and continues producing future workers until canceled.

Scheduled dispatch always normalizes the worker start policy to `StartAndReturnAfterAccepted`. Background scheduling therefore starts work even if the definition or retained override specified `DoNotStart`, and it never blocks the scheduling loop waiting for a worker to start or finish. Other current definition settings and retained schedule overrides still apply normally.

The SQL store coordinates due schedules across hosts that share a persistence scope and logical work system. Delivery is at least once: a failure after queueing but before recording the occurrence can cause another host to dispatch it again. Use Workable idempotency or application-level idempotency when duplicate execution is unsafe. Cancellation succeeds only before dispatch begins; after that boundary it returns `Conflict`.

## Query And Cancel

```csharp
WorkScheduleSnapshot? schedule = await session.Schedules.Get(scheduleId, cancellationToken);

WorkScheduleQueryResult active = await session.Schedules.List(
    new WorkScheduleCriteria(
        DefinitionName: "tests.run",
        Status: WorkScheduleStatus.Active,
        Take: 100),
    cancellationToken);

while (active.Cursor is not null)
{
    active = await session.Schedules.List(
        new WorkScheduleCriteria(Status: WorkScheduleStatus.Active, Take: 100, Cursor: active.Cursor),
        cancellationToken);
}

WorkScheduleOccurrenceQueryResult history = await session.Schedules.ListOccurrences(
    scheduleId,
    take: 100,
    cancellationToken);

WorkScheduleOverviewResult overview = await session.Schedules.GetOverview(
    new WorkScheduleOverviewCriteria(
        SelectedScheduleId: scheduleId,
        RecentScheduleTake: 100,
        UpcomingScheduleTake: 100,
        OccurrenceTake: 50),
    cancellationToken);

WorkScheduleUpcomingQueryResult upcoming = await session.Schedules.ListUpcoming(
    take: 100,
    cursor: overview.Upcoming.Cursor,
    cancellationToken);

WorkScheduleCancellationOutcome canceled = await session.Schedules.Cancel(
    scheduleId,
    cancellationToken);
```

The equivalent HTTP management routes are:

- `GET /workable/schedules?definitionName={name}&status={status}&take={count}`
- `GET /workable/schedules/upcoming?take={count}&cursorNextRunAt={instant}&cursorScheduleId={scheduleId}`
- `GET /workable/schedules/overview?selectedScheduleId={scheduleId}&recentTake={count}&upcomingTake={count}&occurrenceTake={count}`
- `GET /workable/schedules/{scheduleId}`
- `GET /workable/schedules/{scheduleId}/occurrences?take={count}`
- `POST /workable/schedules/{scheduleId}/cancel`

Named systems use the same paths under `/workable/systems/{systemName}`. Schedule queries are paged. Continue a recent-schedule page with its `createdAt` and schedule-id cursor, or an upcoming page with its `nextRunAt` and schedule-id cursor. Both cursor fields must be supplied together. Occurrence-history queries return up to 100 entries. List and overview results contain schedule summaries; use the schedule-detail route when retained input or worker options are needed. Read authorization is applied to each target definition, and unknown or non-visible schedules return `404`. Cancellation uses the target definition's current cancel authorization.

Only an active schedule can be canceled. Canceling a completed or already canceled schedule returns `Conflict`; an unknown or non-visible schedule returns `NotFound`. Once dispatch begins, cancellation returns `Conflict` and does not cancel the worker being created or already queued; control that worker through the normal worker-action surface.
