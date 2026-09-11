using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

/// <summary>
/// Measures SQL-backed schedule creation as retained schedule metadata grows.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineSqlScheduleCreationBenchmarks
{
    private const string Scope = "schedule-creation-benchmark";
    private SqlScheduleBenchmarkStore fixture = null!;
    private WorkScheduleStoreCreateRequest request = null!;

    [Params(0, 1_000, 10_000)]
    public int RetainedScheduleCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.fixture = SqlScheduleBenchmarkStore.Create(Scope).GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture.Reset().GetAwaiter().GetResult();
        this.fixture.SeedSchedules(
                this.RetainedScheduleCount,
                SqlScheduleSeedShape.Completed)
            .GetAwaiter()
            .GetResult();
        var record = this.fixture.CreateRecord(
            WorkScheduleId.New(),
            DateTimeOffset.UtcNow + TimeSpan.FromDays(1));
        this.request = SqlScheduleBenchmarkStore.CreateStoreRequest(
            record,
            maximumRetainedSchedules: this.RetainedScheduleCount + 2,
            maximumRetainedSchedulesPerActor: this.RetainedScheduleCount + 2);
    }

    [Benchmark]
    public Task<WorkScheduleStoreCreationStatus> CreateSchedule()
        => this.fixture.Store.Create(this.request);

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.Reset().GetAwaiter().GetResult();
}

/// <summary>
/// Measures occurrence completion and quota enforcement as retained history grows.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineSqlScheduleHistoryCompletionBenchmarks
{
    private const string Scope = "schedule-history-completion-benchmark";
    private SqlScheduleBenchmarkStore fixture = null!;
    private WorkScheduleClaim claim = null!;
    private WorkScheduleClaimCompletion completion = null!;

    [Params(0, 10_000, 100_000)]
    public int RetainedOccurrenceCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.fixture = SqlScheduleBenchmarkStore.Create(Scope).GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture.Reset().GetAwaiter().GetResult();
        var now = DateTimeOffset.UtcNow;
        var scheduleId = WorkScheduleId.New();
        this.fixture.Store.Create(SqlScheduleBenchmarkStore.CreateStoreRequest(
                this.fixture.CreateRecord(scheduleId, now - TimeSpan.FromMinutes(1))))
            .GetAwaiter()
            .GetResult();
        this.fixture.SeedOccurrences(scheduleId, this.RetainedOccurrenceCount)
            .GetAwaiter()
            .GetResult();
        this.claim = this.fixture.Store.ClaimDue(new(
                SqlScheduleBenchmarkStore.SystemName,
                now,
                TimeSpan.FromMinutes(1),
                MaximumCount: 1))
            .GetAwaiter()
            .GetResult()
            .Single();
        if (!this.fixture.Store.BeginDispatch(new(
                SqlScheduleBenchmarkStore.SystemName,
                scheduleId,
                this.claim.LeaseId,
                now,
                now + TimeSpan.FromMinutes(1)))
            .GetAwaiter()
            .GetResult())
        {
            throw new InvalidOperationException("Expected the benchmark schedule dispatch to start.");
        }

        this.completion = SqlScheduleBenchmarkStore.CreateCompletion(
            this.claim,
            maximumRetainedOccurrences: Math.Max(1, this.RetainedOccurrenceCount));
    }

    [Benchmark]
    public Task CompleteOccurrenceAtHistoryLimit()
        => this.fixture.Store.CompleteClaim(this.completion);

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.Reset().GetAwaiter().GetResult();
}

/// <summary>
/// Compares sequential and concurrent occurrence completion inside one work system.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineSqlScheduleCompletionContentionBenchmarks
{
    private const string Scope = "schedule-completion-contention-benchmark";
    private const int RetainedOccurrenceCount = 10_000;
    private SqlScheduleBenchmarkStore fixture = null!;
    private WorkScheduleClaimCompletion[] completions = null!;

    [Params(1, 8, 25)]
    public int CompletionCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.fixture = SqlScheduleBenchmarkStore.Create(Scope).GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture.Reset().GetAwaiter().GetResult();
        var now = DateTimeOffset.UtcNow;
        var scheduleIds = this.fixture.SeedSchedules(
                this.CompletionCount,
                SqlScheduleSeedShape.ActiveDue,
                now - TimeSpan.FromMinutes(1))
            .GetAwaiter()
            .GetResult();
        this.fixture.SeedOccurrences(scheduleIds[0], RetainedOccurrenceCount)
            .GetAwaiter()
            .GetResult();
        var claims = this.fixture.Store.ClaimDue(new(
                SqlScheduleBenchmarkStore.SystemName,
                now,
                TimeSpan.FromMinutes(1),
                MaximumCount: this.CompletionCount))
            .GetAwaiter()
            .GetResult();
        if (claims.Count != this.CompletionCount)
        {
            throw new InvalidOperationException(
                $"Expected {this.CompletionCount} benchmark claims but received {claims.Count}.");
        }

        foreach (var claim in claims)
        {
            if (!this.fixture.Store.BeginDispatch(new(
                    SqlScheduleBenchmarkStore.SystemName,
                    claim.Record.Schedule.Id,
                    claim.LeaseId,
                    now,
                    now + TimeSpan.FromMinutes(1)))
                .GetAwaiter()
                .GetResult())
            {
                throw new InvalidOperationException("Expected the benchmark schedule dispatch to start.");
            }
        }

        this.completions = claims
            .Select(claim => SqlScheduleBenchmarkStore.CreateCompletion(
                claim,
                maximumRetainedOccurrences: 200_000))
            .ToArray();
    }

    [Benchmark(Baseline = true)]
    public async Task CompleteOccurrencesSequentially()
    {
        foreach (var completion in this.completions)
        {
            await this.fixture.Store.CompleteClaim(completion);
        }
    }

    [Benchmark]
    public Task CompleteOccurrencesConcurrently()
        => Task.WhenAll(this.completions.Select(completion => this.fixture.Store.CompleteClaim(completion)));

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.Reset().GetAwaiter().GetResult();
}

/// <summary>
/// Measures the schedule-list queries used by the administration UI.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BaselineSqlScheduleQueryBenchmarks
{
    private const string Scope = "schedule-query-benchmark";
    private SqlScheduleBenchmarkStore fixture = null!;

    [Params(1_000, 10_000)]
    public int RetainedScheduleCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.fixture = SqlScheduleBenchmarkStore.Create(Scope).GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture.Reset().GetAwaiter().GetResult();
        this.fixture.SeedSchedules(
                this.RetainedScheduleCount,
                SqlScheduleSeedShape.Mixed)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<WorkScheduleSummary>> ListRecentSchedules()
        => this.fixture.Store.List(new(
            SqlScheduleBenchmarkStore.SystemName,
            Take: WorkScheduleCriteria.MaximumTake));

    [Benchmark]
    public Task<IReadOnlyList<WorkScheduleSummary>> ListActiveSchedules()
        => this.fixture.Store.List(new(
            SqlScheduleBenchmarkStore.SystemName,
            Status: WorkScheduleStatus.Active,
            Take: WorkScheduleCriteria.MaximumTake));

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.Reset().GetAwaiter().GetResult();
}

/// <summary>
/// Measures recent occurrence-history queries as one schedule approaches its retention limit.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BaselineSqlScheduleOccurrenceQueryBenchmarks
{
    private const string Scope = "schedule-occurrence-query-benchmark";
    private SqlScheduleBenchmarkStore fixture = null!;
    private WorkScheduleId scheduleId;

    [Params(100, 10_000, 100_000)]
    public int RetainedOccurrenceCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.fixture = SqlScheduleBenchmarkStore.Create(Scope).GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture.Reset().GetAwaiter().GetResult();
        this.scheduleId = this.fixture.SeedSchedules(1, SqlScheduleSeedShape.Completed)
            .GetAwaiter()
            .GetResult()[0];
        this.fixture.SeedOccurrences(this.scheduleId, this.RetainedOccurrenceCount)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public Task<IReadOnlyList<WorkScheduleOccurrence>> ListRecentOccurrences()
        => this.fixture.Store.ListOccurrences(new(
            SqlScheduleBenchmarkStore.SystemName,
            this.scheduleId,
            Take: 100));

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.Reset().GetAwaiter().GetResult();
}

/// <summary>
/// Measures the real scheduler loop while a simultaneous due-work backlog is drained.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineSqlScheduleBacklogBenchmarks
{
    private const string Scope = "schedule-backlog-benchmark";
    private const string DefinitionName = "perf.schedule.backlog";
    private SqlScheduleBenchmarkStore fixture = null!;
    private ServiceProvider provider = null!;
    private IWorkSystem system = null!;
    private WorkRequestContext requestContext = null!;
    private bool started;

    [Params(25, 100, 1_000)]
    public int DueScheduleCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => this.fixture = SqlScheduleBenchmarkStore.Create(Scope).GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        this.fixture.Reset().GetAwaiter().GetResult();
        this.fixture.SeedSchedules(
                this.DueScheduleCount,
                SqlScheduleSeedShape.ActiveDue,
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
                DefinitionName)
            .GetAwaiter()
            .GetResult();

        var services = new ServiceCollection();
        services.AddWorkableSqlServerPersistence(new WorkableSqlServerPersistenceOptions
        {
            ConnectionString = this.fixture.ConnectionString,
            SchemaName = SqlScheduleBenchmarkStore.SchemaName,
            PersistenceScope = Scope,
            AutoDeploySchema = false,
        });
        services.AddWorkableSystem(SqlScheduleBenchmarkStore.SystemName, builder => builder
            .RequireAuthorization(false)
            .UseScheduling(new WorkSystemSchedulingConfiguration
            {
                IsEnabled = true,
                MaximumActiveSchedules = 2_000,
                MaximumActiveSchedulesPerDefinition = 2_000,
                MaximumActiveSchedulesPerActor = 2_000,
                MaximumRetainedSchedules = 10_000,
                MaximumRetainedSchedulesPerActor = 10_000,
                MaximumDispatchesPerBatch = 25,
            })
            .AddWork(
                WorkDefinition.Create(DefinitionName),
                static (_, _, _) => Task.FromResult(WorkExecutionResult.Success())));
        this.provider = services.BuildServiceProvider();
        this.system = this.provider.GetRequiredService<IWorkSystemRegistry>().Default;
        this.requestContext = BenchmarkRequestContexts.CreateAnonymous("Drain scheduled benchmark work.");
        this.started = false;
    }

    [Benchmark]
    public async Task<int> StartAndDrainDueSchedules()
    {
        await this.system.Start(this.requestContext);
        this.started = true;
        try
        {
            return await this.fixture.WaitForOccurrenceCount(
                this.DueScheduleCount,
                TimeSpan.FromMinutes(3));
        }
        finally
        {
            await this.system.Stop(this.requestContext);
            this.started = false;
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (this.started)
        {
            this.system.Stop(this.requestContext).GetAwaiter().GetResult();
        }

        this.provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
        => this.fixture.Reset().GetAwaiter().GetResult();
}

/// <summary>
/// Compares in-memory queue throughput with SQL persistence registered and scheduling disabled or idle.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineIdleSchedulerQueueBenchmarks
{
    private const int WorkerCount = 100_000;
    private const int Parallelism = 16;
    private const string Scope = "schedule-idle-queue-benchmark";
    private const string DefinitionName = "perf.schedule.idle.queue";
    private string connectionString = null!;
    private ServiceProvider provider = null!;
    private IWorkSystem system = null!;
    private IWorkSystemSession session = null!;
    private WorkRequestContext requestContext = null!;

    [Params(false, true)]
    public bool SchedulingEnabled { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var environment = BenchmarkSqlServerEnvironment.GetShared().GetAwaiter().GetResult();
        this.connectionString = environment.ConnectionString;
        BenchmarkSqlServerEnvironment.PrepareSchema(
                this.connectionString,
                SqlScheduleBenchmarkStore.SchemaName,
                resetStore: false)
            .GetAwaiter()
            .GetResult();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        var services = new ServiceCollection();
        services.AddWorkableSqlServerPersistence(
            this.connectionString,
            SqlScheduleBenchmarkStore.SchemaName,
            Scope,
            autoDeploySchema: false);
        services.AddWorkableSystem("schedule-idle-queue", builder =>
        {
            builder.RequireAuthorization(false);
            if (this.SchedulingEnabled)
            {
                builder.EnableScheduling();
            }

            builder.AddWork(
                WorkDefinition.Create(DefinitionName),
                static (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration.DoNotStart());
        });
        this.provider = services.BuildServiceProvider();
        this.system = this.provider.GetRequiredService<IWorkSystemRegistry>().Default;
        this.requestContext = BenchmarkRequestContexts.CreateAnonymous("Measure idle scheduler queue overhead.");
        this.system.Start(this.requestContext).GetAwaiter().GetResult();
        this.session = this.system.CreateSession(this.requestContext).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task<int> QueueWorkers()
    {
        var accepted = 0;
        for (var offset = 0; offset < WorkerCount; offset += Parallelism)
        {
            var count = Math.Min(Parallelism, WorkerCount - offset);
            var handles = await Task.WhenAll(Enumerable.Range(offset, count)
                .Select(index => this.session.Queue.Enqueue(
                    DefinitionName,
                    WorkableBenchmarkSystem.CreateInput(index))));
            accepted += handles.Count(static handle => handle.QueueOutcome.IsAccepted);
        }

        return accepted;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        this.system.Stop(this.requestContext).GetAwaiter().GetResult();
        this.provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

internal enum SqlScheduleSeedShape
{
    Completed,
    ActiveDue,
    Mixed,
}

internal sealed class SqlScheduleBenchmarkStore
{
    public const string SchemaName = "workable_perf_scheduling";
    public const string SystemName = "schedule-benchmarks";
    public const string DefaultDefinitionName = "perf.schedule.work";
    private const long PayloadSizeBytes = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly WorkActor Actor = new("schedule-benchmark", "Schedule Benchmark");
    private static readonly WorkRequestContext RequestContext = WorkRequestContext.Create(
        WorkInvocationChannel.InProcess,
        Actor,
        "Run SQL scheduling benchmark.",
        isAuthenticated: true);

    private readonly string persistenceScope;

    private SqlScheduleBenchmarkStore(
        string connectionString,
        string persistenceScope,
        WorkableSqlServerScheduleStore store)
    {
        this.ConnectionString = connectionString;
        this.persistenceScope = persistenceScope;
        this.Store = store;
    }

    public string ConnectionString { get; }

    public WorkableSqlServerScheduleStore Store { get; }

    public static async Task<SqlScheduleBenchmarkStore> Create(string persistenceScope)
    {
        var environment = await BenchmarkSqlServerEnvironment.GetShared();
        await BenchmarkSqlServerEnvironment.PrepareSchema(
            environment.ConnectionString,
            SchemaName,
            resetStore: false);
        var options = new WorkableSqlServerPersistenceOptions
        {
            ConnectionString = environment.ConnectionString,
            SchemaName = SchemaName,
            PersistenceScope = persistenceScope,
            AutoDeploySchema = false,
        };
        var store = new WorkableSqlServerScheduleStore(options);
        await store.Initialize(new(SystemName));
        return new(environment.ConnectionString, persistenceScope, store);
    }

    public WorkSchedulePersistenceRecord CreateRecord(
        WorkScheduleId scheduleId,
        DateTimeOffset runAt,
        string definitionName = DefaultDefinitionName)
        => new(
            new WorkScheduleSnapshot(
                scheduleId,
                SystemName,
                definitionName,
                WorkScheduleTiming.Once(runAt),
                Input: null,
                WorkerOptions: null,
                WorkScheduleStatus.Active,
                DateTimeOffset.UtcNow,
                Actor,
                runAt,
                LastRunAt: null,
                CanceledAt: null,
                CanceledBy: null),
            RequestContext,
            WorkScheduleExecutionGrant.Unrestricted);

    public static WorkScheduleStoreCreateRequest CreateStoreRequest(
        WorkSchedulePersistenceRecord record,
        int maximumRetainedSchedules = WorkSystemSchedulingConfiguration.DefaultMaximumRetainedSchedules,
        int maximumRetainedSchedulesPerActor = WorkSystemSchedulingConfiguration.DefaultMaximumRetainedSchedulesPerActor)
        => new(
            record,
            PayloadSizeBytes,
            WorkSystemSchedulingConfiguration.DefaultMaximumSchedulePayloadBytes,
            MaximumActiveSchedules: 20_000,
            MaximumActiveSchedulesPerDefinition: 20_000,
            MaximumActiveSchedulesPerActor: 20_000,
            maximumRetainedSchedules,
            maximumRetainedSchedulesPerActor,
            WorkSystemSchedulingConfiguration.DefaultMaximumRetainedPayloadBytes,
            WorkSystemSchedulingConfiguration.DefaultMaximumRetainedPayloadBytesPerActor);

    public static WorkScheduleClaimCompletion CreateCompletion(
        WorkScheduleClaim claim,
        int maximumRetainedOccurrences)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        return new(
            SystemName,
            claim.Record.Schedule.Id,
            claim.LeaseId,
            new WorkScheduleOccurrence(
                Guid.NewGuid(),
                claim.Record.Schedule.Id,
                claim.ScheduledAt,
                attemptedAt,
                WorkScheduleOccurrenceStatus.Accepted,
                WorkQueueStatus.Accepted,
                WorkerId.New(),
                [],
                attemptedAt + TimeSpan.FromDays(1)),
            WorkScheduleStatus.Active,
            attemptedAt + TimeSpan.FromDays(1),
            maximumRetainedOccurrences,
            WorkSystemSchedulingConfiguration.DefaultMaximumRetainedOccurrencePayloadBytes);
    }

    public async Task Reset()
    {
        await using var connection = new SqlConnection(this.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
DELETE FROM [{SchemaName}].[WorkSchedules]
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;
DELETE FROM [{SchemaName}].[WorkScheduleHosts]
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;
DELETE FROM [{SchemaName}].[WorkScheduleOccurrenceUsage]
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;
""";
        command.Parameters.AddWithValue("@PersistenceScope", this.persistenceScope);
        command.Parameters.AddWithValue("@WorkSystemName", SystemName);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<WorkScheduleId[]> SeedSchedules(
        int count,
        SqlScheduleSeedShape shape,
        DateTimeOffset? dueAt = null,
        string definitionName = DefaultDefinitionName)
    {
        if (count == 0)
        {
            return [];
        }

        await this.GetOrCreateOccurrenceUsageId();
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, count).Select(_ => WorkScheduleId.New()).ToArray();
        var table = CreateScheduleTable();
        var timingJson = Serialize(WorkScheduleTiming.Once(dueAt ?? now + TimeSpan.FromDays(1)));
        var requestContextJson = Serialize(new
        {
            RequestContext.Origin,
            RequestContext.Description,
            RequestContext.Url,
            RequestContext.IsAuthenticated,
        });
        var actorJson = Serialize(Actor);
        var executionGrantJson = Serialize(WorkScheduleExecutionGrant.Unrestricted);
        var actorKey = SHA256.HashData(Encoding.UTF8.GetBytes(Actor.Id!));
        for (var index = 0; index < count; index++)
        {
            var active = shape == SqlScheduleSeedShape.ActiveDue ||
                (shape == SqlScheduleSeedShape.Mixed && index % 2 == 0);
            var nextRunAt = active
                ? dueAt ?? now + TimeSpan.FromDays(1)
                : (DateTimeOffset?)null;
            table.Rows.Add(
                ids[index].Value,
                this.persistenceScope,
                SystemName,
                definitionName,
                timingJson,
                DBNull.Value,
                DBNull.Value,
                requestContextJson,
                active ? WorkScheduleStatus.Active.ToString() : WorkScheduleStatus.Completed.ToString(),
                now.AddTicks(-index),
                DbValue(nextRunAt),
                active ? DBNull.Value : now,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                false,
                executionGrantJson,
                actorJson,
                actorKey,
                PayloadSizeBytes,
                0L);
        }

        await this.WriteBulk("WorkSchedules", table);
        return ids;
    }

    public async Task SeedOccurrences(WorkScheduleId scheduleId, int count)
    {
        if (count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var occurrenceUsageId = await this.GetOrCreateOccurrenceUsageId();
        var table = CreateOccurrenceTable();
        for (var index = 0; index < count; index++)
        {
            var attemptedAt = now.AddTicks(-index);
            table.Rows.Add(
                Guid.NewGuid(),
                occurrenceUsageId,
                scheduleId.Value,
                attemptedAt,
                attemptedAt,
                WorkScheduleOccurrenceStatus.Accepted.ToString(),
                WorkQueueStatus.Accepted.ToString(),
                Guid.NewGuid(),
                "[]",
                now + TimeSpan.FromDays(1),
                2L);
        }

        await this.WriteBulk("WorkScheduleOccurrences", table);
        await using var connection = new SqlConnection(this.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
UPDATE [{SchemaName}].[WorkScheduleOccurrenceUsage]
SET OccurrenceCount = OccurrenceCount + @OccurrenceCount,
    PayloadSizeBytes = PayloadSizeBytes + @PayloadSizeBytes
WHERE OccurrenceUsageId = @OccurrenceUsageId;
""";
        command.Parameters.AddWithValue("@OccurrenceCount", count);
        command.Parameters.AddWithValue("@PayloadSizeBytes", checked(count * 2L));
        command.Parameters.AddWithValue("@OccurrenceUsageId", occurrenceUsageId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> WaitForOccurrenceCount(int expected, TimeSpan timeout)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            await using var connection = new SqlConnection(this.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
SELECT COUNT(1)
FROM [{SchemaName}].[WorkScheduleOccurrences] occurrences
INNER JOIN [{SchemaName}].[WorkSchedules] schedules
    ON schedules.ScheduleId = occurrences.ScheduleId
WHERE schedules.PersistenceScope = @PersistenceScope
  AND schedules.WorkSystemName = @WorkSystemName;
""";
            command.Parameters.AddWithValue("@PersistenceScope", this.persistenceScope);
            command.Parameters.AddWithValue("@WorkSystemName", SystemName);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (count >= expected)
            {
                return count;
            }

            // Keep completion observation out of the scheduler's hot path. Polling SQL at
            // scheduler cadence turns this benchmark into a scheduler-plus-observer load test.
            await Task.Delay(250);
        }

        throw new TimeoutException($"Timed out waiting for {expected:N0} scheduled occurrences.");
    }

    private async Task WriteBulk(string tableName, DataTable table)
    {
        using var bulk = new SqlBulkCopy(this.ConnectionString)
        {
            DestinationTableName = $"[{SchemaName}].[{tableName}]",
            BatchSize = 10_000,
            BulkCopyTimeout = 120,
        };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table);
    }

    private static DataTable CreateScheduleTable()
    {
        var table = new DataTable();
        table.Columns.Add("ScheduleId", typeof(Guid));
        table.Columns.Add("PersistenceScope", typeof(string));
        table.Columns.Add("WorkSystemName", typeof(string));
        table.Columns.Add("DefinitionName", typeof(string));
        table.Columns.Add("TimingJson", typeof(string));
        table.Columns.Add("InputJson", typeof(string));
        table.Columns.Add("WorkerOptionsJson", typeof(string));
        table.Columns.Add("RequestContextJson", typeof(string));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("CreatedAt", typeof(DateTimeOffset));
        table.Columns.Add("NextRunAt", typeof(DateTimeOffset));
        table.Columns.Add("LastRunAt", typeof(DateTimeOffset));
        table.Columns.Add("CanceledAt", typeof(DateTimeOffset));
        table.Columns.Add("CanceledByJson", typeof(string));
        table.Columns.Add("LeaseId", typeof(Guid));
        table.Columns.Add("LeaseExpiresAt", typeof(DateTimeOffset));
        table.Columns.Add("DispatchStarted", typeof(bool));
        table.Columns.Add("ExecutionGrantJson", typeof(string));
        table.Columns.Add("CreatedByJson", typeof(string));
        table.Columns.Add("CreatedByKey", typeof(byte[]));
        table.Columns.Add("PayloadSizeBytes", typeof(long));
        table.Columns.Add("CancellationPayloadReserveBytes", typeof(long));
        return table;
    }

    private static DataTable CreateOccurrenceTable()
    {
        var table = new DataTable();
        table.Columns.Add("OccurrenceId", typeof(Guid));
        table.Columns.Add("OccurrenceUsageId", typeof(Guid));
        table.Columns.Add("ScheduleId", typeof(Guid));
        table.Columns.Add("ScheduledAt", typeof(DateTimeOffset));
        table.Columns.Add("AttemptedAt", typeof(DateTimeOffset));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("QueueStatus", typeof(string));
        table.Columns.Add("WorkerId", typeof(Guid));
        table.Columns.Add("MessagesJson", typeof(string));
        table.Columns.Add("ExpiresAt", typeof(DateTimeOffset));
        table.Columns.Add("PayloadSizeBytes", typeof(long));
        return table;
    }

    private async Task<Guid> GetOrCreateOccurrenceUsageId()
    {
        await using var connection = new SqlConnection(this.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
MERGE [{SchemaName}].[WorkScheduleOccurrenceUsage] WITH (HOLDLOCK) AS target
USING (SELECT @PersistenceScope AS PersistenceScope, @WorkSystemName AS WorkSystemName) AS source
ON target.PersistenceScope = source.PersistenceScope
AND target.WorkSystemName = source.WorkSystemName
WHEN NOT MATCHED THEN INSERT
    (OccurrenceUsageId, PersistenceScope, WorkSystemName, OccurrenceCount, PayloadSizeBytes)
    VALUES (NEWID(), source.PersistenceScope, source.WorkSystemName, 0, 0);
SELECT OccurrenceUsageId
FROM [{SchemaName}].[WorkScheduleOccurrenceUsage]
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;
""";
        command.Parameters.AddWithValue("@PersistenceScope", this.persistenceScope);
        command.Parameters.AddWithValue("@WorkSystemName", SystemName);
        return (Guid)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scheduling benchmark occurrence usage row was not created."));
    }

    private static object DbValue(DateTimeOffset? value)
        => value is { } actual ? actual : DBNull.Value;

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);
}
