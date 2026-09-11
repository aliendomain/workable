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

`UseScheduling(...)` also configures denial-of-service bounds for active and terminal records. The defaults are 10,000 retained schedules per system, 2,000 per creator id, 1 MiB for one serialized schedule, 256 MiB retained per system, and 64 MiB retained per creator id. Schedule admission reserves a small, fixed part of each record's byte budget for the bounded cancellation identity; successful cancellation replaces that reserve, while another terminal transition releases it. Cancellation therefore cannot grow retained storage beyond the quota accepted at creation. Occurrence history is independently bounded to 100,000 records, 64 KiB of messages per occurrence, and 64 MiB of occurrence messages per system. Each scheduler host claims and concurrently dispatches at most 25 schedules and 8 MiB of serialized schedule payload in one batch, then immediately claims another batch while due work remains. The one-second fallback poll is used only while idle so schedules committed by another process are discovered even without an in-process notification. An occurrence-history query returns at most 100 rows and 4 MiB of retained messages. When an occurrence would cross its rolling count or byte budget, the oldest occurrences are removed atomically before the new one is inserted. Canceled and completed schedules continue counting until `HistoryRetention` cleanup removes them, so repeated create/cancel operations cannot bypass admission limits. Cleanup drains as many as ten 1,000-row batches per minute. Limits must be positive, the per-occurrence payload limit must be at least two bytes for an empty JSON array when larger messages must be omitted, the claim budget cannot be smaller than one schedule, and the occurrence-query budget cannot be smaller than one occurrence. Admission and occurrence trimming are atomic in the SQL store even when several hosts act concurrently:

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

The built-in HTTP adapter exposes `POST /workable/work/{definitionName}/schedules`, and the admin UI surfaces it through **Schedule** in the queue dialog whenever host discovery reports `schedulingAvailable`. The dialog supports a local first-run date and time, intervals in minutes, hours, or days, and five-field cron expressions with an explicit IANA time zone. Cron input is checked by the server and the dialog previews the next five occurrences before creation. It reuses the same input and worker-option form as immediate queueing. The system tree's **Schedules** screen lists upcoming active work plus the 1,000 most recently created retained schedules, displays recent dispatch history, links accepted dispatches to their workers, and supports creating or canceling schedules. When retained history fills that recent window, the screen follows the active query's continuation cursor through bounded pages so every active schedule remains visible and cancelable even when the configured active limit exceeds 1,000.

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

When `RunMissedExecution` is `true`, startup dispatches one overdue occurrence and advances a recurring schedule directly to its next future interval or cron occurrence. It does not emit a burst for every elapsed occurrence. When the option is `false`, an occurrence that became due while no scheduler host was available is recorded as `Skipped` and the same advancement rule applies. Availability is tracked per host and per logical work system, so a newly started host does not skip valid overdue work merely because another host created it or remained healthy during a rolling deployment. Host observation is piggybacked on the existing claim poll rather than adding another database round trip; a short availability lease covers abrupt process loss, and graceful shutdown ends the host interval immediately. The startup boundary is captured only after the work system has finished initializing and is ready to begin scheduler polling, so work that becomes due during a true full-system startup remains missed.

Schedule occurrence status describes dispatch, not eventual execution:

- `Accepted` means the normal Workable queue accepted a worker.
- `Rejected` retains the queue outcome and safe messages.
- `Skipped` records the missed-run policy decision.
- `Failed` records an unexpected dispatch failure without persisting raw exception details.

After queue acceptance, the worker follows its current retry, failed-worker handling, auto-cancel, recurrence, logging, and retention policies. A recurring schedule remains independent of the result of an earlier worker and continues producing future workers until canceled.

Scheduled dispatch always normalizes the worker start policy to `StartAndReturnAfterAccepted`. Background scheduling therefore starts work even if the definition or retained override specified `DoNotStart`, and it never blocks the scheduling loop waiting for a worker to start or finish. Other current definition settings and retained schedule overrides still apply normally.

The SQL store leases due schedules so multiple hosts sharing the same persistence scope and Workable system do not normally dispatch the same occurrence concurrently. Immediately before queueing, the scheduler atomically validates its unexpired lease and marks dispatch as begun. Cancellation can commit only before this marker; after it, cancellation returns `Conflict`, so an accepted cancellation cannot be followed by queueing from an already claimed occurrence. An expired or superseded claimant does not dispatch. Delivery remains at least once across a narrow failure window: if queueing succeeds but saving the occurrence fails, another host can claim it after lease expiry. Use Workable idempotency or application-level idempotency for work where duplicate execution is unsafe.

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

WorkScheduleCancellationOutcome canceled = await session.Schedules.Cancel(
    scheduleId,
    cancellationToken);
```

The equivalent HTTP management routes are:

- `GET /workable/schedules?definitionName={name}&status={status}&take={count}`
- `GET /workable/schedules/{scheduleId}`
- `GET /workable/schedules/{scheduleId}/occurrences?take={count}`
- `POST /workable/schedules/{scheduleId}/cancel`

Named systems use the same paths under `/workable/systems/{systemName}`. Schedule queries accept between one and 1,000 rows. A full page includes a `cursor`; pass its `createdAt` and `scheduleId.value` back as `cursorCreatedAt` and `cursorScheduleId` to fetch the next page. Both cursor query parameters are required together. Occurrence-history queries accept between one and 100 rows and are also constrained by `MaximumOccurrenceQueryPayloadBytes`. Schedule lists return bounded summaries and omit retained input and worker options; fetch one schedule by id when those details are needed. The list applies read authorization per target definition before paging; detail and history return `404` for unknown or non-visible schedules. Cancellation applies the target definition's current cancel authorization. Creator and canceler actor id, name, and email fields are each limited to 512 characters; Workable rejects an oversized actor instead of truncating security identity data before durable audit or per-actor quota enforcement. The SQL schedule store also rejects definition names longer than its 450-character column bound with a structured invalid-creation outcome instead of allowing a truncation exception to escape.

Only an active schedule can be canceled. Canceling a completed or already canceled schedule returns `Conflict`; an unknown or non-visible schedule returns `NotFound`. A due row is revalidated immediately before dispatch, so cancellation accepted after claiming but before the atomic dispatch-start marker suppresses that stale claim. Once dispatch is marked as begun, cancellation returns `Conflict` and does not retroactively cancel a worker being created or already queued; control that worker through the normal worker-action surface.
