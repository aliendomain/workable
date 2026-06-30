using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

internal sealed class DurableWorkflowBenchmarkSystem : IAsyncDisposable
{
    private const string SystemName = "workflow-benchmarks";
    private const string SchemaName = "workable_perf";
    private static readonly TimeSpan BenchmarkWorkflowChildPurgeInterval = TimeSpan.FromMilliseconds(100);
    private readonly ServiceProvider provider;
    private readonly WorkRequestContext requestContext;
    private readonly string schemaName;

    private DurableWorkflowBenchmarkSystem(
        ServiceProvider provider,
        IWorkSystem system,
        WorkRequestContext requestContext,
        string connectionString,
        string schemaName)
    {
        this.provider = provider;
        this.System = system;
        this.requestContext = requestContext;
        this.ConnectionString = connectionString;
        this.schemaName = schemaName;
    }

    public IWorkSystem System { get; }

    public string ConnectionString { get; }

    public string DurabilitySchemaName => this.schemaName;

    public static async Task<DurableWorkflowBenchmarkSystem> Create(
        int branchCount,
        Func<int, Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>>> childExecutorFactory,
        string? durabilityConnectionString = null,
        string schemaName = SchemaName,
        bool resetStore = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(childExecutorFactory);

        var connectionString = durabilityConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var sql = await BenchmarkSqlServerEnvironment.GetShared();
            connectionString = sql.ConnectionString;
        }

        await BenchmarkSqlServerEnvironment.PrepareSchema(
            connectionString,
            schemaName,
            resetStore,
            cancellationToken);

        var services = new ServiceCollection();
        services.AddWorkableSqlServerDurableQueue(connectionString, schemaName);
        services.AddWorkableSystem(SystemName, builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("perf.workflow.durable.dispatch.child", category: "Perf:Workflow"),
                childExecutorFactory(0),
                configuration => configuration
                    .QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1))
                    .ConfigureRetention(purgeInterval: BenchmarkWorkflowChildPurgeInterval));

            for (var index = 0; index < Math.Max(1, branchCount); index++)
            {
                builder.AddWork(
                    WorkDefinition.Create($"perf.workflow.durable.parallel.child.{index:D2}", category: "Perf:Workflow"),
                    childExecutorFactory(index),
                    configuration => configuration
                        .QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1))
                        .ConfigureRetention(purgeInterval: BenchmarkWorkflowChildPurgeInterval));
            }

            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "perf.workflow.durable.dispatch",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("perf.workflow.durable.dispatch.child")));

            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "perf.workflow.durable.parallel",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow =>
                {
                    workflow.RunParallel("parallel", parallel =>
                    {
                        for (var index = 0; index < Math.Max(1, branchCount); index++)
                        {
                            parallel.DispatchWork(
                                $"branch-{index:D2}",
                                WorkDefinition.Create($"perf.workflow.durable.parallel.child.{index:D2}"));
                        }
                    });
                    workflow.Join("join");
                });
        });

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var system = registry.TryGet(SystemName, out var namedSystem)
            ? namedSystem
            : throw new InvalidOperationException($"Expected benchmark Workable system '{SystemName}'.");
        var requestContext = BenchmarkRequestContexts.CreateAnonymous("Run durable workflow performance benchmark.");
        await system.Start(requestContext, cancellationToken);
        return new DurableWorkflowBenchmarkSystem(
            provider,
            system,
            requestContext,
            connectionString,
            schemaName);
    }

    public static async Task<WorkflowRunStatus> WaitForFinalStatus(
        IWorkSystem system,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(60))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = WorkflowBenchmarkReflection.GetStatus(system, runId);
            if (status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled)
            {
                return status.Value;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException(
            $"Timed out waiting for workflow run '{runId:D}' to reach a final state. {WorkflowBenchmarkReflection.DescribeRuns(system, [runId])}");
    }

    public static async Task<DurableStateCounts> WaitForDurableState(
        string connectionString,
        string schemaName,
        Func<DurableStateCounts, bool> predicate,
        CancellationToken cancellationToken = default)
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
            $"Timed out waiting for durable workflow state to settle. WorkEntries={latest.WorkEntries}, WorkflowRuns={latest.WorkflowRuns}.");
    }

    public static async Task WaitForSignal(
        Task signal,
        string description,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out waiting for {description}.", exception);
        }
    }

    public static async Task ExpireDurableWorkerLeases(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
UPDATE {QuoteIdentifier(schemaName)}.[WorkQueueEntries]
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET());
""";
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string QuoteIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    public readonly record struct DurableStateCounts(
        long WorkEntries,
        long WorkflowRuns);
}
