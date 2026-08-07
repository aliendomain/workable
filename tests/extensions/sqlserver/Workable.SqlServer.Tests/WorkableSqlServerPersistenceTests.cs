using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workable;
using Workable.SqlServer;
using Xunit.Abstractions;

namespace Workable.Tests;

[Collection(nameof(SqlServerTestHostCollection))]
[Trait("Category", "SqlServerIntegration")]
[Trait("Category", "PersistenceIntegration")]
public sealed class WorkableSqlServerPersistenceTests : IAsyncLifetime
{
    private const string SchemaName = "workable";
    private static readonly JsonSerializerOptions DurableJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ITestOutputHelper output;
    private readonly SqlServerTestHost sqlServer;
    private readonly string databaseName = "WorkableTests_" + Guid.NewGuid().ToString("N");

    public WorkableSqlServerPersistenceTests(
        SqlServerTestHost sqlServer,
        ITestOutputHelper output)
    {
        this.sqlServer = sqlServer;
        this.output = output;
        this.ConnectionString = sqlServer.BuildConnectionString(this.databaseName);
    }

    private string ConnectionString { get; }

    public async Task InitializeAsync()
    {
        this.output.WriteLine($"SQL Server test host: {this.sqlServer.Description}");

        await using var connection = new SqlConnection(this.sqlServer.MasterConnectionString);
        await connection.OpenAsync();
        await Execute(connection, $"CREATE DATABASE {Quote(this.databaseName)};");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(this.sqlServer.MasterConnectionString);
        await connection.OpenAsync();
        await Execute(connection, $"""
IF DB_ID(N'{Escape(this.databaseName)}') IS NOT NULL
BEGIN
    ALTER DATABASE {Quote(this.databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {Quote(this.databaseName)};
END
""");
    }

    [Fact]
    public async Task StartInitializesWorkEntriesSchema()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-schema",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart());

        await system.Start();
        await StopWithTimeout(system);

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkEntries';
"""));
        Assert.Equal(5, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND columns.name IN (N'IsDurableQueued', N'HasIdempotencyReservation', N'HasPersistentConcurrency', N'ClaimedAt', N'ConcurrencyBucket');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkQueueEntries';
"""));
        Assert.Equal(6, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND columns.name IN (N'WorkerId', N'HasPersistentConcurrency', N'ConcurrencyScope', N'ConcurrencyMaximumCapacity', N'LeaseExpiresAt', N'ConcurrencyBucket');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'IX_WorkableWorkEntries_PersistentConcurrencyReady';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'IX_WorkableWorkEntries_Concurrency';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Ready';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_PersistentConcurrencyReady';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Concurrency';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'UX_WorkableWorkEntries_Idempotency'
  AND indexes.filter_definition LIKE N'%HasIdempotencyReservation%';
"""));
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND columns.name IN (N'WorkSystemId', N'DefinitionId');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkflowRuns';
"""));
        Assert.Equal(8, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkflowRuns'
  AND columns.name IN (N'PersistenceScope', N'DefinitionFingerprint', N'RequestContextJson', N'WorkflowInputJson', N'StepsJson', N'PendingControlAction', N'PendingControlRequestContextJson', N'UpdatedAt');
"""));
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkflowRuns'
  AND columns.name = N'WorkSystemId';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkflowRuns'
  AND indexes.name = N'IX_WorkableWorkflowRuns_Recovery';
"""));
    }

    [Fact]
    public async Task GeneratedSchemaScriptRunsWhenSessionStartsWithQuotedIdentifierOff()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var connection = await this.OpenConnection();
        await Execute(connection, "SET QUOTED_IDENTIFIER OFF;");
        foreach (var batch in WorkableSqlServerSchema.CreateBatches(SchemaName))
        {
            await Execute(connection, batch);
        }

        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'IX_WorkableWorkEntries_Ready';
"""));
    }

    [Fact]
    public async Task AutoDeploySchemaCanBeDisabledWhenSchemaAlreadyExists()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);

        var system = this.CreateSystem(
            "sql-schema-preinstalled",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart(),
            autoDeploySchema: false);

        await system.Start();
        await system.Stop();
    }

    [Fact]
    public async Task AutoDeploySchemaDisabledFailsStartupWhenSchemaIsMissing()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-schema-missing",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart(),
            autoDeploySchema: false);

        var exception = await Assert.ThrowsAsync<WorkableSqlServerSchemaDeploymentException>(() => system.Start());

        Assert.Contains("could not validate schema", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task AutoDeploySchemaFailsClearlyWhenExistingWorkEntriesSchemaIsIncomplete()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var connection = await this.OpenConnection();
        await Execute(connection, "CREATE SCHEMA workable;");
        await Execute(connection, """
CREATE TABLE workable.WorkEntries
(
    WorkerId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkEntries PRIMARY KEY,
    WorkSystemName nvarchar(256) NOT NULL,
    DefinitionName nvarchar(450) NOT NULL
);
""");

        var system = this.CreateSystem(
            "sql-schema-incomplete",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart());

        var exception = await Assert.ThrowsAsync<WorkableSqlServerSchemaDeploymentException>(() => system.Start());

        Assert.Contains("could not deploy schema", exception.Message);
        var validation = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("IsDurableQueued", validation.Message);
    }

    [Fact]
    public async Task DurableWorkflowCanStartAndCompleteWithSqlServerPersistence()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.dispatch"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        "workflow.durable.dispatch",
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("sample.dispatch")));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("workflow-tests", out var namedSystem));
        var system = namedSystem!;
        await system.Start();

        var handle = await StartWorkflow(system, "workflow.durable.dispatch");
        var completion = await WaitForWorkflowCompletion(handle);

        Assert.True(IsWorkflowAccepted(handle));
        Assert.Equal(WorkflowRunStatus.Completed.ToString(), WorkflowCompletionStatus(completion));

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
        await TestEventually.Until(
            async () => await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries;
""") == 0,
            "Expected durable workflow child work entries to be cleaned up.");
        await system.Stop();
    }

    [Fact]
    public async Task DurableWorkflowCanceledByShutdownRecoversAfterRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.parallel.recover";
        var alphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    async (_, _, cancellationToken) =>
                    {
                        alphaStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        betaStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await WaitWithTimeout(alphaStarted.Task);
        await WaitWithTimeout(betaStarted.Task);

        await using (var beforeStop = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () => await Scalar<int>(beforeStop, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
""") == 1,
                "Expected the durable workflow run to be persisted before shutdown.");
            await TestEventually.Until(
                async () => await Scalar<int>(beforeStop, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'workflow-tests'
  AND DefinitionName IN (N'sample.alpha', N'sample.beta');
""") == 2,
                "Expected both durable child workers to be persisted before shutdown.");
        }

        await StopWithTimeout(firstSystem);
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));
        await using (var afterStop = await this.OpenConnection())
        {
            Assert.Equal(1, await Scalar<int>(afterStop, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
"""));
        }

        await using (var expired = await this.OpenConnection())
        {
            await Execute(expired, """
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE WorkSystemName = N'workflow-tests'
  AND DefinitionName IN (N'sample.alpha', N'sample.beta');
""");
        }

        var resumedChildren = 0;
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) =>
                    {
                        if (Interlocked.Increment(ref resumedChildren) == 2)
                        {
                            replayed.TrySetResult();
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    (_, _, _) =>
                    {
                        if (Interlocked.Increment(ref resumedChildren) == 2)
                        {
                            replayed.TrySetResult();
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();
        await WaitWithTimeout(replayed.Task);

        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Completed.ToString(),
            "Expected the durable workflow to recover and complete after restart.",
            timeout: TimeSpan.FromSeconds(15));

        await using (var verification = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () => await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
""") == 1,
                "Expected the recovered durable workflow run to remain persisted while its final child workers are still retained.");
            await TestEventually.Until(
                async () => await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'workflow-tests';
""") == 0,
                "Expected recovered durable workflow child workers to be cleaned up after completion.");
        }

        await StopWithTimeout(secondSystem);
        Assert.Equal(2, Volatile.Read(ref resumedChildren));
    }

    [Fact]
    public async Task DurableWorkflowPauseRequestPersistsWhileOutstandingChildRuns()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.stop.persist.sql";
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.stop.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        slowStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.stop.fast"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref fastRuns);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("sample.stop.slow"))
                        .Join("join")
                        .DispatchWork("fast", WorkDefinition.Create("sample.stop.fast")));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("workflow-tests", out var namedSystem));
        var system = namedSystem!;
        await system.Start();

        var handle = await StartWorkflow(system, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await WaitWithTimeout(slowStarted.Task);

        var pause = await ExecuteWorkflowAction(system, runId, "Pause");
        Assert.True(WorkflowActionAccepted(pause));

        await using (var connection = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () =>
                {
                    var pendingAction = await Scalar<string?>(
                        connection,
                        """
SELECT TOP (1) PendingControlAction
FROM workable.WorkflowRuns
WHERE RunId = @RunId;
""",
                        new SqlParameter("@RunId", runId.Value));
                    if (string.Equals(pendingAction, "Pause", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    var status = await Scalar<string?>(
                        connection,
                        """
SELECT TOP (1) Status
FROM workable.WorkflowRuns
WHERE RunId = @RunId;
""",
                        new SqlParameter("@RunId", runId.Value));
                    return string.Equals(status, nameof(WorkflowRunStatus.Paused), StringComparison.Ordinal);
                },
                "Expected the durable workflow pause request to be persisted or applied while the outstanding child is still running.",
                timeout: TimeSpan.FromSeconds(10));
        }

        await system.Stop();
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));
        Assert.Equal(0, Volatile.Read(ref fastRuns));
    }

    [Fact]
    public async Task DurableWorkflowRecoveryOnlyReplaysIncompleteParallelChildren()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.parallel.partial-recover";
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await TestEventually.Until(
            () => WorkflowStepWorkerIds(firstSystem, runId, "join").Count == 1,
            "Expected the durable join step to retain only the unfinished child before shutdown.",
            timeout: TimeSpan.FromSeconds(15));

        var remainingWorkerId = WorkflowStepWorkerIds(firstSystem, runId, "join").Single();
        await StopWithTimeout(firstSystem);
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));
        await using (var afterStop = await this.OpenConnection())
        {
            Assert.Equal(1, await Scalar<int>(afterStop, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
"""));
        }

        await using (var expired = await this.OpenConnection())
        {
            await Execute(expired, $"""
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE WorkerId = '{remainingWorkerId.Value:D}';
""");
        }

        var replayedAlpha = 0;
        var replayedBeta = 0;
        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref replayedAlpha);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref replayedBeta);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();

        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Completed.ToString(),
            "Expected the durable workflow to recover and complete after replaying only the unfinished child.",
            timeout: TimeSpan.FromSeconds(15));

        await StopWithTimeout(secondSystem);
        Assert.NotEqual(Guid.Empty, remainingWorkerId.Value);
        Assert.Equal(0, Volatile.Read(ref replayedAlpha));
        Assert.Equal(1, Volatile.Read(ref replayedBeta));
    }

    [Fact]
    public async Task WorkflowRunViewShowsRecoveredSqlDurableWorkflowState()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.parallel.status-recover";
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await TestEventually.Until(
            () => WorkflowStepWorkerIds(firstSystem, runId, "join").Count == 1,
            "Expected the durable join step to retain only the unfinished child before shutdown.",
            timeout: TimeSpan.FromSeconds(15));

        var remainingWorkerId = WorkflowStepWorkerIds(firstSystem, runId, "join").Single();
        await StopWithTimeout(firstSystem);
        await WaitForWorkflowCompletion(handle);

        await using (var expired = await this.OpenConnection())
        {
            await Execute(expired, $"""
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE WorkerId = '{remainingWorkerId.Value:D}';
""");
        }

        var resumedBetaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedBetaRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        resumedBetaStarted.TrySetResult();
                        await resumedBetaRelease.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();
        await resumedBetaStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var detail = await new WorkflowRunViewAdapter().Run(
            secondSystem,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            runId);

        resumedBetaRelease.TrySetResult();
        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Completed.ToString(),
            "Expected the recovered durable workflow to complete after inspection.",
            timeout: TimeSpan.FromSeconds(15));

        await StopWithTimeout(secondSystem);

        Assert.NotNull(detail);
        Assert.Equal("dispatch", detail!.CurrentStepName);
        Assert.Equal(1, detail.OutstandingChildren.Active);
        Assert.Equal(0, detail.OutstandingChildren.Unavailable);
        Assert.Equal(2, Assert.Single(detail.Steps, step => step.Name == "dispatch").Children.Total);
        Assert.Equal(1, Assert.Single(detail.Steps, step => step.Name == "join").Children.Total);
    }

    [Fact]
    public async Task WorkflowRunsRoundTripThroughTheSqlPersistenceStore()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var run = CreateWorkflowRun("workflow-tests", "workflow.sql.roundtrip");

        await store.UpsertWorkflowRun(run);
        var loaded = new List<WorkflowRunPersistenceRecord>();
        await foreach (var item in store.ListWorkflowRuns(
            new WorkflowPersistenceReadRequest("workflow-tests")))
        {
            loaded.Add(item);
        }

        await store.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(run.RunId));

        Assert.Single(loaded);
        Assert.Equal(run.RunId, loaded[0].RunId);
        Assert.Equal(run.DefinitionName, loaded[0].DefinitionName);
        Assert.Equal(run.DefinitionFingerprint, loaded[0].DefinitionFingerprint);
        Assert.Equal(run.PendingControlAction, loaded[0].PendingControlAction);
        Assert.Equal(run.PendingControlRequestContext, loaded[0].PendingControlRequestContext);
        Assert.Equal(run.Input?.Json, loaded[0].Input?.Json);
        Assert.Equal(run.RequestContext.Actor.Id, loaded[0].RequestContext.Actor.Id);
        Assert.Equal(run.Steps.Single().WorkerIds, loaded[0].Steps.Single().WorkerIds);
        Assert.Equal(run.ChildReceipts.Single().WorkerId, loaded[0].ChildReceipts.Single().WorkerId);
        Assert.Equal(run.ChildReceipts.Single().StepName, loaded[0].ChildReceipts.Single().StepName);
        Assert.Equal(run.ChildReceipts.Single().DefinitionName, loaded[0].ChildReceipts.Single().DefinitionName);
        Assert.Equal(run.ChildReceipts.Single().CompletionStatus, loaded[0].ChildReceipts.Single().CompletionStatus);

        await using var connection = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
    }

    [Fact]
    public async Task WorkflowTransactionCommitsWorkflowRunsAndDurableWorkersAtomically()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var run = CreateWorkflowRun("workflow-tests", "workflow.sql.transaction.commit");
        var workerId = WorkerId.New();

        await using (var transaction = await store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest("workflow-tests")))
        {
            await store.UpsertWorkflowRun(run, transaction);
            await store.Enqueue(CreateDurableEnqueueRequest(
                WorkSystemId.New(),
                "workflow-tests",
                workerId,
                "sample.dispatch",
                "workflow-transaction-commit",
                transaction));

            await transaction.Commit();
        }

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
        Assert.Equal(1, await CountRowsForSubject(connection, "workflow-transaction-commit"));
    }

    [Fact]
    public async Task BatchChecksDurableWorkerExistence()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var alpha = WorkerId.New();
        var beta = WorkerId.New();
        var missing = WorkerId.New();
        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            "workflow-tests",
            alpha,
            "sample.dispatch",
            "batch-existence-alpha",
            transaction: null));
        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            "workflow-tests",
            beta,
            "sample.dispatch",
            "batch-existence-beta",
            transaction: null));

        var existing = await store.DurableWorkersExist([alpha, missing, beta]);

        Assert.True(existing.SetEquals([alpha, beta]));
    }

    [Fact]
    public async Task DurableQueueRoundTripsExplicitProfilingCaptureModes()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var systemName = $"profiling-capture-mode-{Guid.NewGuid():N}";
        const string fullDefinitionName = "profiling-capture-mode-full";
        const string boundedDefinitionName = "profiling-capture-mode-bounded";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var fullWorkerId = WorkerId.New();
        var boundedWorkerId = WorkerId.New();

        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            systemName,
            fullWorkerId,
            fullDefinitionName,
            "profiling-full",
            transaction: null,
            enableIdempotency: false,
            options: new WorkerOptions
            {
                ProfilingEnabled = true,
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            }));
        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            systemName,
            boundedWorkerId,
            boundedDefinitionName,
            "profiling-bounded",
            transaction: null,
            enableIdempotency: false,
            options: new WorkerOptions
            {
                ProfilingEnabled = true,
                ProfilingCaptureMode = WorkProfileCaptureMode.Bounded,
            }));

        var claimed = await ClaimReady(
            store,
            "profiling-consumer",
            batchSize: 10,
            workSystemName: systemName);
        var byWorkerId = claimed.ToDictionary(entry => entry.Lease.WorkerId);

        Assert.Equal(WorkProfileCaptureMode.Full, byWorkerId[fullWorkerId].Options.ProfilingCaptureMode);
        Assert.True(byWorkerId[fullWorkerId].Options.HasExplicitProfilingCaptureMode);
        Assert.Equal(WorkProfileCaptureMode.Bounded, byWorkerId[boundedWorkerId].Options.ProfilingCaptureMode);
        Assert.True(byWorkerId[boundedWorkerId].Options.HasExplicitProfilingCaptureMode);
    }

    [Fact]
    public async Task WorkflowTransactionRollbackDiscardsWorkflowRunsAndDurableWorkersTogether()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var run = CreateWorkflowRun("workflow-tests", "workflow.sql.transaction.rollback");
        var workerId = WorkerId.New();

        await using (var transaction = await store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest("workflow-tests")))
        {
            await store.UpsertWorkflowRun(run, transaction);
            await store.Enqueue(CreateDurableEnqueueRequest(
                WorkSystemId.New(),
                "workflow-tests",
                workerId,
                "sample.dispatch",
                "workflow-transaction-rollback",
                transaction));
        }

        await using var connection = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
        Assert.Equal(0, await CountRowsForSubject(connection, "workflow-transaction-rollback"));
    }

    [Fact]
    public async Task DurableWorkflowRecoveryFailsWhenTheRegisteredDefinitionShapeChanged()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var workflowDefinitionId = WorkflowDefinitionId.New();
        const string workflowName = "workflow.sql.recovery.definition-changed";
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    async (_, _, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        id: workflowDefinitionId,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .DispatchWork("dispatch", WorkDefinition.Create("sample.alpha"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await TestEventually.Until(
            () => WorkflowStatus(firstSystem, runId) == WorkflowRunStatus.Running.ToString(),
            "Expected the durable workflow to start before restart.",
            timeout: TimeSpan.FromSeconds(15));
        await using (var persistedRunConnection = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () => await Scalar<int>(persistedRunConnection, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
""") == 1,
                "Expected the durable workflow run to be persisted before shutdown.",
                timeout: TimeSpan.FromSeconds(15));
            await TestEventually.Until(
                async () => await Scalar<int>(persistedRunConnection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'workflow-tests'
  AND DefinitionName = N'sample.alpha';
""") == 1,
                "Expected the durable workflow child worker to be persisted before shutdown.",
                timeout: TimeSpan.FromSeconds(15));
        }

        await StopWithTimeout(firstSystem);
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));

        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        id: workflowDefinitionId,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .DispatchWork("dispatch", WorkDefinition.Create("sample.alpha"))
                        .DispatchWork("archive", WorkDefinition.Create("sample.beta"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();

        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Failed.ToString(),
            "Expected the changed durable workflow definition to reject recovery of the persisted run.",
            timeout: TimeSpan.FromSeconds(15));

        var snapshot = WorkflowSnapshot(secondSystem, runId)
            ?? throw new InvalidOperationException("Expected failed recovered workflow snapshot.");
        var messages = (System.Collections.IEnumerable)(snapshot.GetType().GetProperty("Messages")?.GetValue(snapshot)
            ?? throw new InvalidOperationException("Expected workflow messages."));
        Assert.Contains(
            messages.Cast<object>(),
            message => string.Equals(
                message.GetType().GetProperty("Code")?.GetValue(message)?.ToString(),
                "workable.workflow.definition_mismatch",
                StringComparison.Ordinal));

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
"""));

        await StopWithTimeout(secondSystem);
    }

    [Fact]
    public async Task DirectSqlClientExecutionDoesNotAppearInWorkerProfileWhenSqlServerProfilingIsNotConfigured()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-direct-command";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.DoesNotContain(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectSqlClientExecutionAppearsInWorkerProfileWhenSqlServerProfilingIsConfigured()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-direct-command";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var sqlNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Equal("sql.client", sqlNode.Instrumentation);
        Assert.Contains("Microsoft.Data.SqlClient", contextJson, StringComparison.Ordinal);
        Assert.Contains("ExecuteScalar", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Statement\":\"SELECT @Value;\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"ParameterCount\":1", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"@Value\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Value\":42", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"IsRedacted\":false", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSqlClientExecutionAddsFailureToItsSingleTimingNode()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-failed-command";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles a failed Microsoft.Data.SqlClient command once.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT * FROM workable.__profiling_missing_table;";
                    try
                    {
                        await command.ExecuteScalarAsync(cancellationToken);
                    }
                    catch (SqlException)
                    {
                        return WorkExecutionResult.Success();
                    }

                    return WorkExecutionResult.Failure([
                        WorkMessage.Error("sql.failure.expected", "The deliberately invalid statement unexpectedly succeeded."),
                    ]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var profile = completion.Worker?.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var nodes = Flatten(profile.Root).ToList();
        var sqlNode = Assert.Single(
            nodes,
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.DoesNotContain(nodes, node => node.Label == "SQL Error");
        Assert.Contains("\"Outcome\":\"Faulted\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Data.SqlClient.SqlException", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"MessageTruncated\":false", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlProfilingUsesSharedAutomaticInstrumentationLimitAndReportsOmissions()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-automatic-limit";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder =>
            {
                builder.ConfigureProfiling(maximumAutomaticInstrumentationNodes: 1);
                builder.AddWork(
                    WorkDefinition.Create(
                        workName,
                        "Bounds automatic SQL profile nodes.",
                        defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                    async (context, input, cancellationToken) =>
                    {
                        await using var connection = new SqlConnection(this.ConnectionString);
                        await connection.OpenAsync(cancellationToken);
                        for (var value = 1; value <= 2; value++)
                        {
                            await using var command = connection.CreateCommand();
                            command.CommandText = "SELECT @Value;";
                            command.Parameters.AddWithValue("@Value", value);
                            await command.ExecuteScalarAsync(cancellationToken);
                        }

                        return WorkExecutionResult.Success();
                    });
            })
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var profile = completion.Worker?.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var summary = Assert.Single(
            Flatten(profile.Root),
            node => node.Label == "Automatic instrumentation truncated");
        Assert.Contains("sql.client", JsonSerializer.Serialize(summary.Context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlProfilingOmitsBinaryParameterValuesFromWorkerProfile()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-full-values";
        var description = new string('x', 320);
        var payload = Enumerable.Range(0, 64)
            .Select(static index => (byte)index)
            .ToArray();
        var statement = """
            SELECT
                @Description AS Description,
                DATALENGTH(@Payload) AS PayloadLength;
            """;

        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution without retaining binary parameter values.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.Parameters.AddWithValue("@Description", description);
                    command.Parameters.Add("@Payload", SqlDbType.VarBinary, payload.Length).Value = payload;

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    await reader.ReadAsync(cancellationToken);
                    var payloadLength = reader.GetInt32(reader.GetOrdinal("PayloadLength"));

                    return payloadLength == payload.Length
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.payload.unexpected", $"Expected {payload.Length} but received {payloadLength}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var sqlNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Contains($"\"Statement\":{JsonSerializer.Serialize(statement)}", contextJson, StringComparison.Ordinal);
        Assert.Contains($"\"Value\":{JsonSerializer.Serialize(description)}", contextJson, StringComparison.Ordinal);
        Assert.Contains($"\"Value\":{JsonSerializer.Serialize("<binary omitted>")}", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(payload), contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlProfilingBoundsLargeStatementsAndOmitsBinaryParameterValues()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-bounded-values";
        var text = new string('t', 8_000);
        var payload = Enumerable.Range(0, 2_048).Select(static index => (byte)index).ToArray();
        var statement = "SELECT LEN(@Text), DATALENGTH(@Payload); --" + new string('s', 40_000);
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Bounds large SQL profile context values.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.Parameters.AddWithValue("@Text", text);
                    command.Parameters.Add("@Payload", SqlDbType.VarBinary, payload.Length).Value = payload;
                    await command.ExecuteScalarAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var sqlNode = Assert.Single(
            Flatten(completion.Worker!.Profile!.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(sqlNode.Context));
        var root = json.RootElement;
        var parameters = root.GetProperty("Parameters").EnumerateArray().ToArray();
        var textParameter = Assert.Single(parameters, parameter => parameter.GetProperty("Name").GetString() == "@Text");
        var binaryParameter = Assert.Single(parameters, parameter => parameter.GetProperty("Name").GetString() == "@Payload");

        Assert.True(root.GetProperty("StatementTruncated").GetBoolean());
        Assert.Equal(8_192, root.GetProperty("Statement").GetString()!.Length);
        Assert.True(textParameter.GetProperty("IsTruncated").GetBoolean());
        Assert.Equal(1_024, textParameter.GetProperty("Value").GetString()!.Length);
        Assert.False(binaryParameter.GetProperty("IsTruncated").GetBoolean());
        Assert.Equal("<binary omitted>", binaryParameter.GetProperty("Value").GetString());
    }

    [Fact]
    public async Task SqlProfilingFinalizesOutstandingCommandsBeforePublishingProfileSnapshot()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-outstanding-command";
        await using var pendingConnection = new SqlConnection(this.ConnectionString);
        await using var pendingCommand = pendingConnection.CreateCommand();
        pendingCommand.CommandText = "WAITFOR DELAY '00:00:01'; SELECT 1;";
        Task<object?>? pendingExecution = null;
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Finalizes an outstanding SQL profile timing.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await pendingConnection.OpenAsync(cancellationToken);
                    pendingExecution = pendingCommand.ExecuteScalarAsync(CancellationToken.None);
                    return WorkExecutionResult.Success();
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));
        var sqlNode = Assert.Single(
            Flatten(completion.Worker!.Profile!.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var beforeCompletion = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Contains("\"Outcome\":\"Incomplete\"", beforeCompletion, StringComparison.Ordinal);
        await (pendingExecution ?? throw new InvalidOperationException("Expected pending SQL execution."));
        Assert.Equal(beforeCompletion, JsonSerializer.Serialize(sqlNode.Context));
    }

    [Fact]
    public async Task ObviousSecretSqlParametersAreRedactedInWorkerProfile()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-secret-parameter";
        const string secretValue = "super-secret-value";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution with secret-like parameters redacted.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT LEN(@Password);";
                    command.Parameters.AddWithValue("@Password", secretValue);

                    var length = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return length == secretValue.Length
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected {secretValue.Length} but received {length}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var sqlNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Contains("\"Name\":\"@Password\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\\u003Credacted\\u003E", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"IsRedacted\":true", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectSqlClientExecutionAppearsInWorkerProfileWhenSqlServerProfilingStartsAfterSqlClientHasInitialized()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using (var connection = await this.OpenConnection())
        {
            Assert.Equal(1, await Scalar<int>(connection, "SELECT 1;"));
        }

        const string workName = "sql-profiled-direct-command-existing-listener";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution after SqlClient already initialized.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.Contains(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SqlProfilingTracksOnlyTheOwningSystemWhenMultipleSystemsAreStarted()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-direct-command-multi-system";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem("alpha", builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution for the owning system only.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .AddWorkableSystem("beta", builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution for the owning system only.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 7);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 7
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 7 but received {value}.")]);
                }))
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var systems = registry.Systems.ToDictionary(system => system.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        await using var alpha = systems["alpha"];
        await using var beta = systems["beta"];

        await alpha.Start();
        await beta.Start();

        var alphaCompletion = await WaitForCompletion(await alpha.Queue.Enqueue(workName));
        var betaCompletion = await WaitForCompletion(await beta.Queue.Enqueue(workName));

        Assert.True(alphaCompletion.IsCompletedSuccessfully);
        Assert.True(betaCompletion.IsCompletedSuccessfully);

        var alphaProfile = alphaCompletion.Worker?.Profile ?? throw new InvalidOperationException("Expected alpha worker profile.");
        var betaProfile = betaCompletion.Worker?.Profile ?? throw new InvalidOperationException("Expected beta worker profile.");

        Assert.Single(
            Flatten(alphaProfile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        Assert.Single(
            Flatten(betaProfile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DurableQueueRetainsImplicitNonProductionProfilingWhenQueueOptionsOnlyOverrideConfiguration()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-durable-dev-default-profiling";
        using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development))
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Uses durable queueing with inherited non-production profiling."),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration.QueueDurably()))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        Assert.True(system.Catalog.TryGet(workName, out var definition));

        var completion = await WaitForCompletion(await system.Queue.Enqueue(
            workName,
            options: new WorkerOptions(definition.Configuration)));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.NotNull(worker.Profile);
    }

    [Fact]
    public async Task DurableIdempotentQueueWritesOneCombinedEntryAndRejectsDuplicate()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-durable-idempotent",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .QueueDurably()
                .CoordinatePersistently().RejectDuplicateSubjects()
                .DoNotStart());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "combined"));
        var first = await system.Queue.Enqueue("sql-durable-idempotent", input);
        var duplicate = await system.Queue.Enqueue("sql-durable-idempotent", input);

        await using var connection = await this.OpenConnection();
        var combinedRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.SubjectType = N'order'
  AND entries.SubjectValue = N'combined'
  AND entries.IsDurableQueued = 0
  AND entries.HasIdempotencyReservation = 1;
""");

        await system.Stop();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Equal(1, combinedRows);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.queue_durability.duplicate");
    }

    [Fact]
    public async Task DurableQueueWithoutIdempotencyWritesDurableRowOnly()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-durable-without-idempotency",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "durable-no-idempotency"));
        var handle = await system.Queue.Enqueue("sql-durable-without-idempotency", input);

        await using var connection = await this.OpenConnection();
        var durableOnlyRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.SubjectType = N'order'
  AND entries.SubjectValue = N'durable-no-idempotency'
  AND entries.IsDurableQueued = 0
  AND entries.HasIdempotencyReservation = 0
  AND queue.HasPersistentConcurrency = 0;
""");

        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(1, durableOnlyRows);
    }

    [Fact]
    public async Task DurableQueueWithPersistentConcurrencyWritesClaimMetadata()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-durable-persistent-concurrency-metadata",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .QueueDurably()
                .LimitConcurrency(
                    maximumCapacity: 1,
                    blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
                    limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart)
                .DoNotStart());
        await system.Start();

        var handle = await system.Queue.Enqueue("sql-durable-persistent-concurrency-metadata");

        await using var connection = await this.OpenConnection();
        var durableConcurrencyRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE DefinitionName = N'sql-durable-persistent-concurrency-metadata'
  AND HasPersistentConcurrency = 1
  AND ConcurrencyScope = N'PerDefinition'
  AND ConcurrencyMaximumCapacity = 1;
""");

        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(1, durableConcurrencyRows);
    }

    [Fact]
    public async Task ConcurrentNonTransactionalStoreEnqueuesPersistDurableRows()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const int rowCount = 96;
        const string systemName = "sql-store-batched-enqueue-system";
        const string definitionName = "sql-store-batched-enqueue";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var requests = Enumerable.Range(0, rowCount)
            .Select(index => CreateDurableEnqueueRequest(
                systemId,
                systemName,
                WorkerId.New(),
                definitionName,
                $"batched-store-{index}",
                transaction: null,
                enableIdempotency: false))
            .ToArray();

        await Task.WhenAll(requests.Select(request => store.Enqueue(request)));

        await using var connection = await this.OpenConnection();
        var persistedRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.WorkSystemName = N'sql-store-batched-enqueue-system'
  AND entries.DefinitionName = N'sql-store-batched-enqueue'
  AND entries.IsDurableQueued = 0
  AND entries.HasIdempotencyReservation = 0;
""");

        Assert.Equal(rowCount, persistedRows);
    }

    [Fact]
    public async Task ConcurrentBatchedStoreEnqueuePreservesDuplicateSubjectRejection()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string systemName = "sql-store-batched-duplicate-system";
        const string definitionName = "sql-store-batched-duplicate";
        const string subjectValue = "batched-store-duplicate";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var requests = Enumerable.Range(0, 2)
            .Select(_ => CreateDurableEnqueueRequest(
                systemId,
                systemName,
                WorkerId.New(),
                definitionName,
                subjectValue,
                transaction: null,
                enableIdempotency: true))
            .ToArray();

        var results = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                await store.Enqueue(request);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }));

        await using var connection = await this.OpenConnection();
        var persistedRows = await CountRowsForSubject(connection, subjectValue);

        Assert.Equal(1, results.Count(static exception => exception is null));
        Assert.Single(results.OfType<WorkQueueDurabilityDuplicateException>());
        Assert.Equal(1, persistedRows);
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyWithoutDurabilityWritesReservationOnly()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-idempotency-only",
            async (_, _, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            },
            configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "reservation-only"));
        var first = await system.Queue.Enqueue("sql-idempotency-only", input);
        await WaitForWorkerState(system, RequiredWorkerId(first), WorkerState.Running);
        var duplicate = await system.Queue.Enqueue("sql-idempotency-only", input);

        await using var connection = await this.OpenConnection();
        var reservationRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectType = N'order'
  AND SubjectValue = N'reservation-only'
  AND IsDurableQueued = 0
  AND HasIdempotencyReservation = 1;
""");

        release.SetResult();
        await first.WaitForCompletion();
        await WaitForEntryCount(connection, "reservation-only", 0);
        await system.Stop();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Equal(1, reservationRows);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.idempotency.duplicate_subject");
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyWithoutDurabilityRejectsExistingTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-idempotency-only-existing-transaction",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotency-only-transaction"));

        var handle = await system.Queue.Enqueue("sql-idempotency-only-existing-transaction", input, options);
        await transaction.RollbackAsync();

        var reservationRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectType = N'order'
  AND SubjectValue = N'idempotency-only-transaction';
""");

        await system.Stop();

        Assert.False(handle.QueueOutcome.IsAccepted);
        Assert.Null(handle.WorkerId);
        Assert.False(ran.Task.IsCompleted);
        Assert.Equal(0, reservationRows);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Code == "workable.idempotency.persistence_transaction_requires_durable_queue");
    }

    [Fact]
    public async Task DurableQueueUsingExistingTransactionStartsOnlyAfterCommit()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-existing-transaction",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "transactional"));

        var handle = await system.Queue.Enqueue("sql-existing-transaction", input, options);
        await using (var visibilityConnection = await this.OpenConnection())
        {
            Assert.Equal(0, await CountReadableRowsForSubject(visibilityConnection, "transactional"));
        }

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(ran.Task.IsCompleted);
        Assert.Null(await system.Query.Worker(RequiredWorkerId(handle)));

        await transaction.CommitAsync();

        var completion = await WaitForCompletion(handle);
        await system.Stop();

        Assert.True(ran.Task.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueCommitsWithCallerBusinessDataInSameTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string orderId = "business-transaction";
        const string payload = "invoice-ready";
        await using (var setupConnection = await this.OpenConnection())
        {
            await Execute(setupConnection, """
CREATE TABLE dbo.BusinessOrders
(
    OrderId nvarchar(64) NOT NULL CONSTRAINT PK_BusinessOrders PRIMARY KEY,
    Payload nvarchar(64) NOT NULL
);
""");
        }

        var observedPayload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-business-transaction",
            async (_, _, cancellationToken) =>
            {
                await using var readConnection = new SqlConnection(this.ConnectionString);
                await readConnection.OpenAsync(cancellationToken);
                await using var readCommand = readConnection.CreateCommand();
                readCommand.CommandText = """
SELECT Payload
FROM dbo.BusinessOrders
WHERE OrderId = @OrderId;
""";
                readCommand.Parameters.AddWithValue("@OrderId", orderId);

                var value = await readCommand.ExecuteScalarAsync(cancellationToken);
                if (value is string businessPayload)
                {
                    observedPayload.TrySetResult(businessPayload);
                    return WorkExecutionResult.Success();
                }

                return WorkExecutionResult.Failure(
                    [WorkMessage.Error("business.missing", "Business data was not visible to durable work.")]);
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var businessCommand = connection.CreateCommand())
        {
            businessCommand.Transaction = transaction;
            businessCommand.CommandText = """
INSERT INTO dbo.BusinessOrders (OrderId, Payload)
VALUES (@OrderId, @Payload);
""";
            businessCommand.Parameters.AddWithValue("@OrderId", orderId);
            businessCommand.Parameters.AddWithValue("@Payload", payload);
            await businessCommand.ExecuteNonQueryAsync();
        }

        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", orderId));

        var handle = await system.Queue.Enqueue("sql-business-transaction", input, options);
        await using (var visibilityConnection = await this.OpenConnection())
        {
            Assert.Equal(0, await CountReadableRowsForSubject(visibilityConnection, orderId));
        }

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(observedPayload.Task.IsCompleted);
        Assert.Null(await system.Query.Worker(RequiredWorkerId(handle)));

        await transaction.CommitAsync();

        var completion = await WaitForCompletion(handle);
        var seenPayload = await WaitWithTimeout(observedPayload.Task);
        await system.Stop();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(payload, seenPayload);
    }

    [Fact]
    public async Task DurableCompletionCommitsBusinessDataAndDeletesQueueRowInSameTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string orderId = "durable-complete-business";
        const string payload = "packed";
        await using (var setupConnection = await this.OpenConnection())
        {
            await Execute(setupConnection, """
CREATE TABLE dbo.DurableCompletionOrders
(
    OrderId nvarchar(64) NOT NULL CONSTRAINT PK_DurableCompletionOrders PRIMARY KEY,
    Payload nvarchar(64) NOT NULL
);
""");
        }

        var system = this.CreateSystem(
            "sql-durable-complete-business",
            async (context, _, cancellationToken) =>
            {
                await using var connection = await this.OpenConnection();
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO dbo.DurableCompletionOrders (OrderId, Payload)
VALUES (@OrderId, @Payload);
""";
                AddParameter(command, "@OrderId", orderId);
                AddParameter(command, "@Payload", payload);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await context.CompleteDurablyWithSqlServerTransaction(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            },
            configuration => configuration.QueueDurably().CompleteDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", orderId));
        var handle = await system.Queue.Enqueue("sql-durable-complete-business", input);
        var completion = await WaitForCompletion(handle);

        await using var verification = await this.OpenConnection();
        await WaitForEntryCount(verification, orderId, 0);
        var businessPayload = await Scalar<string>(verification, $"""
SELECT Payload
FROM dbo.DurableCompletionOrders
WHERE OrderId = N'{Escape(orderId)}';
""");
        await system.Stop();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(payload, businessPayload);
    }

    [Fact]
    public async Task DurableCompletionRollsBackBusinessDataWhenExecutionFails()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string orderId = "durable-complete-rollback";
        await using (var setupConnection = await this.OpenConnection())
        {
            await Execute(setupConnection, """
CREATE TABLE dbo.DurableCompletionRollbackOrders
(
    OrderId nvarchar(64) NOT NULL CONSTRAINT PK_DurableCompletionRollbackOrders PRIMARY KEY
);
""");
        }

        var system = this.CreateSystem(
            "sql-durable-complete-rollback",
            async (context, _, cancellationToken) =>
            {
                await using var connection = await this.OpenConnection();
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO dbo.DurableCompletionRollbackOrders (OrderId)
VALUES (@OrderId);
""";
                AddParameter(command, "@OrderId", orderId);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.RollbackAsync(CancellationToken.None);
                return WorkExecutionResult.Failure(
                    [WorkMessage.Error("business.failed", "Business work failed.")]);
            },
            configuration => configuration.QueueDurably().CompleteDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", orderId));
        var handle = await system.Queue.Enqueue("sql-durable-complete-rollback", input);
        var completion = await WaitForCompletion(handle);

        await using var verification = await this.OpenConnection();
        await WaitForFailedEntryRetained(verification, orderId);
        var businessRows = await Scalar<int>(verification, $"""
SELECT COUNT(*)
FROM dbo.DurableCompletionRollbackOrders
WHERE OrderId = N'{Escape(orderId)}';
""");
        await system.Stop();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(0, businessRows);
    }

    [Fact]
    public async Task DurableQueueUsingExistingTransactionWithQuotedIdentifierOffDequeuesAfterCommit()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-quoted-identifier-off",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await Execute(connection, """
SET QUOTED_IDENTIFIER OFF;
SET ANSI_NULLS OFF;
""");
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "quoted-identifier-off"));

        var handle = await system.Queue.Enqueue("sql-quoted-identifier-off", input, options);
        await transaction.CommitAsync();

        var completion = await WaitForCompletion(handle);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(ran.Task.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueUsingExistingTransactionRollbackLeavesNoWorkerOrRow()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-existing-transaction-rollback",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "rollback"));

        var handle = await system.Queue.Enqueue("sql-existing-transaction-rollback", input, options);
        var workerId = RequiredWorkerId(handle);
        await using (var visibilityConnection = await this.OpenConnection())
        {
            Assert.Equal(0, await CountReadableRowsForSubject(visibilityConnection, "rollback"));
        }

        await transaction.RollbackAsync();

        var rows = await CountRowsForSubject(connection, "rollback");
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(ran.Task.IsCompleted);
        Assert.Null(await system.Query.Worker(workerId));
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task DurableQueueReplaysAcrossRestartUsingStableNames()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-restart-durable";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably());
        await firstSystem.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "restart"));

        var handle = await firstSystem.Queue.Enqueue(workName, input, options);
        var workerId = RequiredWorkerId(handle);
        await firstSystem.Stop();
        await transaction.CommitAsync();

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());

        await secondSystem.Start();
        await WaitWithTimeout(ran.Task);
        var replayed = await secondSystem.Query.Worker(workerId);
        await secondSystem.Stop();

        Assert.NotNull(replayed);
        Assert.Equal(workName, replayed.DefinitionName);
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyRejectsDuplicateAcrossRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-idempotency-restart";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .CoordinatePersistently().RejectDuplicateSubjects()
                .DoNotStart());
        await firstSystem.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotency-restart"));
        var first = await firstSystem.Queue.Enqueue(workName, input);
        await firstSystem.Stop();

        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .CoordinatePersistently().RejectDuplicateSubjects()
                .DoNotStart());
        await secondSystem.Start();

        var duplicate = await secondSystem.Queue.Enqueue(workName, input);
        await secondSystem.Stop();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.idempotency.duplicate_subject");
    }

    [Fact]
    public async Task DurableIdempotencyIsIsolatedByWorkSystemName()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-shared-work-name";
        var registry = this.CreateRegistry(
            services => services
                .AddWorkableSystem(builder => builder.AddWork(
                    WorkDefinition.Create(workName, "Default system shared durable work."),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration
                        .QueueDurably()
                        .CoordinatePersistently().RejectDuplicateSubjects()
                        .DoNotStart()))
                .AddWorkableSystem("background", builder => builder.AddWork(
                    WorkDefinition.Create(workName, "Named system shared durable work."),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration
                        .QueueDurably()
                        .CoordinatePersistently().RejectDuplicateSubjects()
                        .DoNotStart())));
        var defaultSystem = registry.Default;
        Assert.True(registry.TryGet("background", out var backgroundSystem));

        await defaultSystem.Start();
        await backgroundSystem.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "shared"));
        var defaultHandle = await defaultSystem.Queue.Enqueue(workName, input);
        var backgroundHandle = await backgroundSystem.Queue.Enqueue(workName, input);
        var defaultDuplicate = await defaultSystem.Queue.Enqueue(workName, input);

        await using var connection = await this.OpenConnection();
        var rows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectValue = N'shared';
""");
        var defaultRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'default'
  AND DefinitionName = N'sql-shared-work-name'
  AND SubjectValue = N'shared';
""");
        var backgroundRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'background'
  AND DefinitionName = N'sql-shared-work-name'
  AND SubjectValue = N'shared';
""");

        await backgroundSystem.Stop();
        await defaultSystem.Stop();

        Assert.True(defaultHandle.QueueOutcome.IsAccepted);
        Assert.True(backgroundHandle.QueueOutcome.IsAccepted);
        Assert.False(defaultDuplicate.QueueOutcome.IsAccepted);
        Assert.Equal(2, rows);
        Assert.Equal(1, defaultRows);
        Assert.Equal(1, backgroundRows);
    }

    [Fact]
    public async Task DurableQueueDeletesRowAfterCompletion()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-final-delete",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "final-delete"));
        var handle = await system.Queue.Enqueue("sql-final-delete", input);
        var completion = await WaitForCompletion(handle);

        await using var connection = await this.OpenConnection();
        await WaitForEntryCount(connection, "final-delete", 0);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueRetainsFailedRowUntilCancelOrComplete()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-failed-retention",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                [WorkMessage.Error("sql.failed", "Failed durable work.")])),
            configuration => configuration.QueueDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "failed-retention"));
        var handle = await system.Queue.Enqueue("sql-failed-retention", input);
        var workerId = RequiredWorkerId(handle);
        var completion = await WaitForCompletion(handle);

        await using var connection = await this.OpenConnection();
        await WaitForFailedEntryRetained(connection, "failed-retention");
        var failed = await system.Query.Worker(workerId);
        Assert.NotNull(failed);
        var cancel = await system.Workers.Execute(new WorkerVersion(workerId, failed.Revision), WorkAction.Cancel);
        await WaitForEntryCount(connection, "failed-retention", 0);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.True(cancel.IsAccepted);
    }

    [Fact]
    public async Task DurableQueueInterruptedByShutdownReplaysAfterLeaseExpires()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-shutdown-interrupted-replay";
        const string subjectValue = "shutdown-interrupted";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSystem = this.CreateSystem(
            workName,
            async (context, _, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                }
                catch (OperationCanceledException)
                {
                    if (context.IsInterrupted)
                    {
                        interruptedSeen.TrySetResult();
                    }

                    throw;
                }
            },
            configuration => configuration.QueueDurably());
        await firstSystem.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue));
        var handle = await firstSystem.Queue.Enqueue(workName, input);
        await WaitWithTimeout(started.Task);

        var stop = await firstSystem.Stop();
        var interrupted = Assert.Single(stop.CancellationRequestedWorkers);
        await WaitWithTimeout(interruptedSeen.Task);

        await using (var connection = await this.OpenConnection())
        {
            await WaitForEntryCount(connection, subjectValue, 1);
            await ExpireLease(connection, subjectValue);
        }

        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                replayed.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await secondSystem.Start();
        await WaitWithTimeout(replayed.Task);

        await using (var verification = await this.OpenConnection())
        {
            await WaitForEntryCount(verification, subjectValue, 0);
        }

        await secondSystem.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(WorkerState.Interrupted, interrupted.State);
    }

    [Fact]
    public async Task ExpiredDurableLeaseIsClaimedAgainAfterRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-expired-lease";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        var workerId = WorkerId.New();
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                workerId,
                workName,
                "expired-lease",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                claimedBy: "dead-owner",
                leaseId: "dead-lease",
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());

        await system.Start();
        await WaitWithTimeout(ran.Task);
        var replayed = await system.Query.Worker(workerId);
        await system.Stop();

        Assert.NotNull(replayed);
        Assert.Equal(workName, replayed.DefinitionName);
    }

    [Fact]
    public async Task CompetingDurableConsumersClaimReadyRowsWithoutOverlap()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-competing-consumers";
        const int batchSize = 150;
        const int rowCount = batchSize * 2;
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            for (var index = 0; index < rowCount; index++)
            {
                await InsertDurableRow(
                    connection,
                    WorkerId.New(),
                    workName,
                    $"competing-{index}",
                    DateTimeOffset.UtcNow.AddMilliseconds(index));
            }
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var first = ClaimReady(store, "consumer-one", batchSize);
        var second = ClaimReady(store, "consumer-two", batchSize);
        var claimed = await Task.WhenAll(first, second);
        var retryOwner = claimed[0].Count <= claimed[1].Count ? "consumer-one" : "consumer-two";
        var retry = await ClaimReady(store, retryOwner, batchSize);
        var firstWorkerIds = claimed[0]
            .Concat(retryOwner == "consumer-one" ? retry : [])
            .Select(entry => entry.Lease.WorkerId)
            .ToHashSet();
        var secondWorkerIds = claimed[1]
            .Concat(retryOwner == "consumer-two" ? retry : [])
            .Select(entry => entry.Lease.WorkerId)
            .ToHashSet();

        await using var verification = await this.OpenConnection();
        var claimedRows = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE ClaimedBy IN (N'consumer-one', N'consumer-two');
""");
        var firstRows = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE ClaimedBy = N'consumer-one';
""");
        var secondRows = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE ClaimedBy = N'consumer-two';
""");

        Assert.Equal(rowCount, firstWorkerIds.Count + secondWorkerIds.Count);
        Assert.Empty(firstWorkerIds.Intersect(secondWorkerIds));
        Assert.Equal(rowCount, claimedRows);
        Assert.True(firstRows > 0);
        Assert.True(secondRows > 0);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyLimitsDurableClaimsAcrossConsumers()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-first",
                DateTimeOffset.UtcNow,
                input: DurableConcurrencyInput("persistent-concurrency-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                input: DurableConcurrencyInput("persistent-concurrency-second"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        var secondClaim = await ClaimReady(store, "consumer-two", batchSize: 10);
        await store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(firstClaim.Single().Lease.WorkerId, firstClaim.Single().Lease)]);
        var claimAfterCompletion = await ClaimReady(store, "consumer-two", batchSize: 10);

        await using var verification = await this.OpenConnection();
        var executingBuckets = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE ConcurrencyBucket = N'Executing';
""");

        Assert.Equal(firstWorkerId, firstClaim.Single().Lease.WorkerId);
        Assert.Empty(secondClaim);
        Assert.Equal(secondWorkerId, claimAfterCompletion.Single().Lease.WorkerId);
        Assert.Equal(1, executingBuckets);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyWithoutIdempotencyLimitsDurableClaims()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-no-idempotency";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-no-idempotency-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-no-idempotency-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-no-idempotency-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-no-idempotency-second"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        var secondClaim = await ClaimReady(store, "consumer-two", batchSize: 10);

        Assert.Equal(firstWorkerId, firstClaim.Single().Lease.WorkerId);
        Assert.Empty(secondClaim);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyAllowsConfiguredCapacity()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-capacity";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var thirdWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(maximumCapacity: 2, enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-capacity-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-capacity-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-capacity-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-capacity-second"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                thirdWorkerId,
                workName,
                "persistent-concurrency-capacity-third",
                DateTimeOffset.UtcNow.AddMilliseconds(2),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-capacity-third"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        var blockedClaim = await ClaimReady(store, "consumer-two", batchSize: 10);
        await store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(firstClaim[0].Lease.WorkerId, firstClaim[0].Lease)]);
        var claimAfterCompletion = await ClaimReady(store, "consumer-two", batchSize: 10);

        Assert.Equal([firstWorkerId, secondWorkerId], firstClaim.Select(entry => entry.Lease.WorkerId));
        Assert.Empty(blockedClaim);
        Assert.Equal(thirdWorkerId, claimAfterCompletion.Single().Lease.WorkerId);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyPerDefinitionLimitsOnlyMatchingDefinition()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string firstWorkName = "sql-persistent-concurrency-definition-a";
        const string secondWorkName = "sql-persistent-concurrency-definition-b";
        var firstWorkerId = WorkerId.New();
        var blockedWorkerId = WorkerId.New();
        var otherDefinitionWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(
            scope: WorkConcurrencyScope.PerDefinition,
            enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                firstWorkName,
                "persistent-concurrency-definition-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                blockedWorkerId,
                firstWorkName,
                "persistent-concurrency-definition-blocked",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                otherDefinitionWorkerId,
                secondWorkName,
                "persistent-concurrency-definition-other",
                DateTimeOffset.UtcNow.AddMilliseconds(2),
                hasIdempotencyReservation: false,
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claimed = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Equal([firstWorkerId, otherDefinitionWorkerId], claimed.Select(entry => entry.Lease.WorkerId));
        Assert.DoesNotContain(claimed, entry => entry.Lease.WorkerId == blockedWorkerId);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyPerSubjectLimitsOnlyMatchingSubject()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-subject";
        var firstWorkerId = WorkerId.New();
        var blockedWorkerId = WorkerId.New();
        var otherSubjectWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(
            scope: WorkConcurrencyScope.PerSubject,
            enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-subject-shared",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                blockedWorkerId,
                workName,
                "persistent-concurrency-subject-shared",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                otherSubjectWorkerId,
                workName,
                "persistent-concurrency-subject-other",
                DateTimeOffset.UtcNow.AddMilliseconds(2),
                hasIdempotencyReservation: false,
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claimed = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Equal([firstWorkerId, otherSubjectWorkerId], claimed.Select(entry => entry.Lease.WorkerId));
        Assert.DoesNotContain(claimed, entry => entry.Lease.WorkerId == blockedWorkerId);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencySerializesConcurrentClaimers()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-race";
        const int rowCount = 12;
        var configuration = DurablePersistenceConcurrencyConfiguration(enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            for (var index = 0; index < rowCount; index++)
            {
                await InsertDurableRow(
                    connection,
                    WorkerId.New(),
                    workName,
                    $"persistent-concurrency-race-{index}",
                    DateTimeOffset.UtcNow.AddMilliseconds(index),
                    hasIdempotencyReservation: false,
                    input: DurableConcurrencyInput($"persistent-concurrency-race-{index}"),
                    configuration: configuration);
            }
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claims = await Task.WhenAll(
            ClaimReady(store, "consumer-one", batchSize: 10),
            ClaimReady(store, "consumer-two", batchSize: 10),
            ClaimReady(store, "consumer-three", batchSize: 10));
        var claimed = claims.SelectMany(entries => entries).ToList();

        Assert.Single(claimed);
    }

    [Fact]
    public async Task RetainingFailedPersistenceBackedConcurrencyRowReleasesCapacity()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-retain-failed";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-retain-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-retain-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-retain-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-retain-second"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        await store.RetainFailed([new WorkQueueDurabilityCleanupRequest(firstClaim.Single().Lease.WorkerId, firstClaim.Single().Lease)]);
        var claimAfterRetain = await ClaimReady(store, "consumer-two", batchSize: 10);

        await using var verification = await this.OpenConnection();
        var retainedRows = await Scalar<int>(verification, $"""
SELECT COUNT(*)
FROM workable.WorkEntries entries
LEFT JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.WorkerId = '{firstWorkerId.Value}'
  AND entries.IsDurableQueued = 0
  AND queue.WorkerId IS NULL;
""");

        Assert.Equal(firstWorkerId, firstClaim.Single().Lease.WorkerId);
        Assert.Equal(secondWorkerId, claimAfterRetain.Single().Lease.WorkerId);
        Assert.Equal(1, retainedRows);
    }

    [Fact]
    public async Task RowsNotMarkedDurableQueuedAreNotClaimed()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                WorkerId.New(),
                "sql-not-queue-payload",
                "not-queue-payload",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                isDurableQueued: false,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claimed = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Empty(claimed);
    }

    [Fact]
    public async Task DurableRowBeingCompletedInTransactionCannotBeClaimed()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string leaseId = "complete-lease";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                workerId,
                "sql-completing-transaction",
                "completing-transaction",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                claimedBy: "completing-owner",
                leaseId: leaseId,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        await using var completionConnection = await this.OpenConnection();
        await using var completionTransaction = await completionConnection.BeginTransactionAsync();
        await store.DeleteFinal(
            [new WorkQueueDurabilityCleanupRequest(
                workerId,
                new WorkQueueDurabilityLease(workerId, "completing-owner", leaseId))],
            new WorkableSqlServerQueueDurabilityTransaction(completionConnection, completionTransaction));

        var claimedWhileCompleting = await ClaimReady(store, "consumer", batchSize: 10);
        await completionTransaction.CommitAsync();

        await using var verification = await this.OpenConnection();
        await WaitForEntryCount(verification, "completing-transaction", 0);

        Assert.Empty(claimedWhileCompleting);
    }

    [Fact]
    public async Task DurableCompletionRollbackLeavesDurableRowClaimable()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string leaseId = "rollback-lease";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                workerId,
                "sql-completion-rollback",
                "completion-rollback",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                claimedBy: "rollback-owner",
                leaseId: leaseId,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        await using var completionConnection = await this.OpenConnection();
        await using var completionTransaction = await completionConnection.BeginTransactionAsync();
        await store.DeleteFinal(
            [new WorkQueueDurabilityCleanupRequest(
                workerId,
                new WorkQueueDurabilityLease(workerId, "rollback-owner", leaseId))],
            new WorkableSqlServerQueueDurabilityTransaction(completionConnection, completionTransaction));
        await completionTransaction.RollbackAsync();

        var claimedAfterRollback = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Equal(workerId, claimedAfterRollback.Single().Lease.WorkerId);
    }

    [Fact]
    public async Task RenewingLostDurableLeaseThrows()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-lost-lease-renew";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await InsertDurableRow(
            connection,
            workerId,
            workName,
            "lost-lease-renew",
            DateTimeOffset.UtcNow,
            leaseId: "current-lease",
            leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var exception = await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            store.RenewLeases(
                [new WorkQueueDurabilityLease(workerId, "old-owner", "stale-lease")],
                TimeSpan.FromMinutes(1)));

        Assert.Equal(workerId, Assert.Single(exception.Leases).WorkerId);
    }

    [Fact]
    public async Task RenewingDurableLeasesUsesSetBasedPartialLossDetection()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-batch-lease-renew";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        var originalExpiry = DateTimeOffset.UtcNow.AddSeconds(5);
        await InsertDurableRow(
            connection,
            firstWorkerId,
            workName,
            "batch-renew-kept",
            DateTimeOffset.UtcNow,
            leaseId: "kept-lease",
            leaseExpiresAt: originalExpiry);
        await InsertDurableRow(
            connection,
            secondWorkerId,
            workName,
            "batch-renew-lost",
            DateTimeOffset.UtcNow.AddMilliseconds(1),
            leaseId: "current-lease",
            leaseExpiresAt: originalExpiry);

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var keptLease = new WorkQueueDurabilityLease(firstWorkerId, "owner", "kept-lease");
        var staleLease = new WorkQueueDurabilityLease(secondWorkerId, "owner", "stale-lease");

        var exception = await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            store.RenewLeases([keptLease, staleLease], TimeSpan.FromMinutes(1)));
        var keptExpiry = await ReadLeaseExpiry(connection, firstWorkerId);
        var lostExpiry = await ReadLeaseExpiry(connection, secondWorkerId);

        Assert.Equal(secondWorkerId, Assert.Single(exception.Leases).WorkerId);
        Assert.True(keptExpiry > originalExpiry);
        Assert.Equal(originalExpiry.ToUnixTimeSeconds(), lostExpiry.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task StaleDurableLeaseCannotDeleteCurrentRow()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-stale-delete";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await InsertDurableRow(
            connection,
            workerId,
            workName,
            "stale-delete",
            DateTimeOffset.UtcNow,
            leaseId: "current-lease",
            leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(
                workerId,
                new WorkQueueDurabilityLease(workerId, "old-owner", "stale-lease"))]));

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
SELECT COUNT(*)
FROM workable.WorkEntries
        WHERE WorkerId = @WorkerId;
""";
        countCommand.Parameters.AddWithValue("@WorkerId", workerId.Value);
        var rows = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task StartupReplayDequeuesDurableRowsInBatch()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-startup-batch";
        const int batchSize = 25;
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            for (var index = 0; index < batchSize; index++)
            {
                await InsertDurableRow(
                    connection,
                    WorkerId.New(),
                    workName,
                    $"startup-batch-{index}",
                    DateTimeOffset.UtcNow.AddMilliseconds(index));
            }
        }

        var remaining = batchSize;
        var allRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    allRan.TrySetResult();
                }

                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());

        await StartWithTimeout(system);
        await WaitWithTimeout(allRan.Task);
        await system.Stop();

        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task StartupReplayQueuesDurableRowsInCreatedOrderBeforeStartingDeferredWork()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-startup-order";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            DurableOrderedConfiguration);
        await firstSystem.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);

        await firstSystem.Queue.Enqueue(workName, OrderedInput("first"), options);
        await firstSystem.Queue.Enqueue(workName, OrderedInput("second"), options);
        await SetCreatedAt(connection, transaction, "first", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SetCreatedAt(connection, transaction, "second", new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
        await firstSystem.Stop();
        await transaction.CommitAsync();

        var executionOrder = new List<string>();
        var orderSync = new Lock();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, input, _) =>
            {
                lock (orderSync)
                {
                    var subjectId = input?.SubjectId;
                    executionOrder.Add(subjectId is null ? string.Empty : subjectId.Value.Value);
                    if (executionOrder.Count == 2)
                    {
                        bothStarted.TrySetResult();
                    }
                }

                return Task.FromResult(WorkExecutionResult.Success());
            },
            DurableOrderedConfiguration);

        await secondSystem.Start();
        await WaitWithTimeout(bothStarted.Task);
        await secondSystem.Stop();

        Assert.Equal(["first", "second"], executionOrder);
    }

    private IWorkSystem CreateSystem(
        string name,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure,
        bool autoDeploySchema = true)
    {
        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName,
                autoDeploySchema)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(name, $"SQL Server integration test for {name}."),
                execute,
                configure))
            .BuildServiceProvider();

        return provider.GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private IWorkSystemRegistry CreateRegistry(Func<IServiceCollection, IServiceCollection> configure)
    {
        var services = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName);

        return configure(services)
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>();
    }

    private bool SkipIfUnavailable()
        => false;

    private async Task<SqlConnection> OpenConnection()
    {
        var connection = new SqlConnection(this.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task Execute(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(
        SqlConnection connection,
        string commandText,
        params SqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetCreatedAt(
        SqlConnection connection,
        DbTransaction transaction,
        string subjectValue,
        DateTimeOffset createdAt)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = """
UPDATE workable.WorkEntries
SET CreatedAt = @CreatedAt
WHERE SubjectValue = @SubjectValue;

UPDATE queue
SET CreatedAt = @CreatedAt
FROM workable.WorkQueueEntries queue
INNER JOIN workable.WorkEntries entries
    ON entries.WorkerId = queue.WorkerId
WHERE entries.SubjectValue = @SubjectValue;
""";
        AddParameter(command, "@CreatedAt", createdAt);
        AddParameter(command, "@SubjectValue", subjectValue);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<IReadOnlyList<WorkQueueDurabilityEntry>> ClaimReady(
        IWorkPersistenceStore store,
        string ownerId,
        int batchSize,
        string? workSystemName = null)
    {
        var entries = new List<WorkQueueDurabilityEntry>();
        await foreach (var entry in store.ClaimReady(
            new WorkQueueDurabilityClaimRequest(
                WorkSystemName: workSystemName,
                OwnerId: ownerId,
                BatchSize: batchSize,
                LeaseDuration: TimeSpan.FromMinutes(1))))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async Task InsertDurableRow(
        SqlConnection connection,
        WorkerId workerId,
        string definitionName,
        string subjectValue,
        DateTimeOffset createdAt,
        bool isDurableQueued = true,
        bool hasIdempotencyReservation = true,
        string? claimedBy = null,
        string? leaseId = null,
        DateTimeOffset? leaseExpiresAt = null,
        WorkInput? input = null,
        WorkConfiguration? configuration = null)
    {
        var rowInput = input ?? WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue));
        var rowConfiguration = configuration ?? WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                },
                Idempotency = WorkIdempotencyConfiguration.Default with
                {
                    IsEnabled = true,
                },
            },
        };

        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO workable.WorkEntries
(
    WorkerId,
    WorkSystemName,
    DefinitionName,
    IsDurableQueued,
    HasIdempotencyReservation,
    HasPersistentConcurrency,
    SubjectType,
    SubjectValue,
    ConcurrencyType,
    ConcurrencyValue,
    InputJson,
    OptionsJson,
    ConfigurationJson,
    OriginJson,
    CreatedAt,
    ClaimedBy,
    LeaseId,
    LeaseExpiresAt
)
VALUES
(
    @WorkerId,
    @WorkSystemName,
    @DefinitionName,
    CAST(0 AS bit),
    @HasIdempotencyReservation,
    @HasPersistentConcurrency,
    @SubjectType,
    @SubjectValue,
    @ConcurrencyType,
    @ConcurrencyValue,
    @InputJson,
    @OptionsJson,
    @ConfigurationJson,
    @OriginJson,
    @CreatedAt,
    NULL,
    NULL,
    NULL
);

IF @IsDurableQueued = 1
BEGIN
    INSERT INTO workable.WorkQueueEntries
    (
        WorkerId,
        WorkSystemName,
        DefinitionName,
        HasPersistentConcurrency,
        ConcurrencyScope,
        ConcurrencyMaximumCapacity,
        SubjectType,
        SubjectValue,
        ConcurrencyType,
        ConcurrencyValue,
        CreatedAt,
        ClaimedBy,
        LeaseId,
        LeaseExpiresAt,
        ConcurrencyBucket
    )
    VALUES
    (
        @WorkerId,
        @WorkSystemName,
        @DefinitionName,
        @HasPersistentConcurrency,
        @ConcurrencyScope,
        @ConcurrencyMaximumCapacity,
        @SubjectType,
        @SubjectValue,
        @ConcurrencyType,
        @ConcurrencyValue,
        @CreatedAt,
        @ClaimedBy,
        @LeaseId,
        @LeaseExpiresAt,
        CASE
            WHEN @HasPersistentConcurrency = 1 AND @LeaseId IS NOT NULL THEN N'Executing'
            ELSE NULL
        END
    );
END;
""";
        var hasPersistentConcurrency = rowConfiguration.Coordination.IsPersistentConcurrencyEnabled;
        var subjectId = rowInput.SubjectId;
        command.Parameters.AddWithValue("@WorkerId", workerId.Value);
        command.Parameters.AddWithValue("@WorkSystemName", "default");
        command.Parameters.AddWithValue("@DefinitionName", definitionName);
        command.Parameters.AddWithValue("@IsDurableQueued", isDurableQueued);
        command.Parameters.AddWithValue("@HasIdempotencyReservation", hasIdempotencyReservation);
        command.Parameters.AddWithValue("@HasPersistentConcurrency", hasPersistentConcurrency);
        command.Parameters.AddWithValue(
            "@ConcurrencyScope",
            hasPersistentConcurrency ? rowConfiguration.Coordination.Concurrency.Scope.ToString() : DBNull.Value);
        command.Parameters.AddWithValue(
            "@ConcurrencyMaximumCapacity",
            hasPersistentConcurrency ? rowConfiguration.Coordination.Concurrency.MaximumCapacity : DBNull.Value);
        command.Parameters.AddWithValue("@SubjectType", (object?)subjectId?.Type ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubjectValue", (object?)subjectId?.Value ?? subjectValue);
        var concurrencyKey = rowInput.ConcurrencyKey;
        command.Parameters.AddWithValue("@ConcurrencyType", concurrencyKey is null ? DBNull.Value : concurrencyKey.Value.Type);
        command.Parameters.AddWithValue("@ConcurrencyValue", concurrencyKey is null ? DBNull.Value : concurrencyKey.Value.Value);
        command.Parameters.AddWithValue(
            "@InputJson",
            JsonSerializer.Serialize(
                rowInput,
                DurableJsonOptions));
        command.Parameters.AddWithValue("@OptionsJson", JsonSerializer.Serialize(WorkerOptions.Default, DurableJsonOptions));
        command.Parameters.AddWithValue(
            "@ConfigurationJson",
            JsonSerializer.Serialize(rowConfiguration, DurableJsonOptions));
        command.Parameters.AddWithValue(
            "@OriginJson",
            JsonSerializer.Serialize(
                WorkOrigin.Create(WorkInvocationChannel.InProcess),
                DurableJsonOptions));
        command.Parameters.AddWithValue("@CreatedAt", createdAt);
        command.Parameters.AddWithValue("@ClaimedBy", (object?)claimedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@LeaseId", (object?)leaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("@LeaseExpiresAt", (object?)leaseExpiresAt ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    private static void DurableOrderedConfiguration(IWorkConfigurationBuilder configuration)
        => configuration
            .QueueDurably()
            .LimitConcurrency(
                maximumCapacity: 1,
                scope: WorkConcurrencyScope.PerConcurrencyKey,
                blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
                limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart);

    private static WorkConfiguration DurablePersistenceConcurrencyConfiguration(
        int maximumCapacity = 1,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerConcurrencyKey,
        bool enableIdempotency = true)
        => WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                },
                Idempotency = WorkIdempotencyConfiguration.Default with
                {
                    IsEnabled = enableIdempotency,
                },
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = maximumCapacity,
                    Scope = scope,
                    BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                    LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                },
            },
        };

    private static WorkInput DurableConcurrencyInput(string subjectValue)
        => WorkInput.Empty
            .WithSubject(new WorkSubjectId("order", subjectValue))
            .WithConcurrencyKey(new WorkConcurrencyKey("persistent-concurrency", "shared"));

    private static WorkInput OrderedInput(string value)
        => WorkInput.Empty
            .WithSubject(new WorkSubjectId("order", value))
            .WithConcurrencyKey(new WorkConcurrencyKey("startup-order", "shared"));

    private static async Task StartWithTimeout(IWorkSystem system)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await system.Start(timeout.Token);
    }

    private static async Task StopWithTimeout(IWorkSystem system)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await system.Stop(cancellationToken: timeout.Token);
    }

    private static async Task<WorkCompletion> WaitForCompletion(IWorkerHandle handle)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await handle.WaitForCompletion(timeout.Token);
    }

    private static async Task WaitWithTimeout(Task task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await task.WaitAsync(timeout.Token);
    }

    private static async Task<T> WaitWithTimeout<T>(Task<T> task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await task.WaitAsync(timeout.Token);
    }

    private static async Task<T> Scalar<T>(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("Expected SQL scalar query to return a value.");
        }

        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<T?> Scalar<T>(
        SqlConnection connection,
        string commandText,
        params SqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            return default;
        }

        return (T?)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static Task<int> CountRowsForSubject(SqlConnection connection, string subjectValue)
        => Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectValue = N'{Escape(subjectValue)}';
""");

    private static Task<int> CountReadableRowsForSubject(SqlConnection connection, string subjectValue)
        => Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkEntries WITH (READPAST)
WHERE SubjectValue = N'{Escape(subjectValue)}';
""");

    private static async Task<DateTimeOffset> ReadLeaseExpiry(SqlConnection connection, WorkerId workerId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT LeaseExpiresAt
FROM workable.WorkQueueEntries
WHERE WorkerId = @WorkerId;
""";
        command.Parameters.AddWithValue("@WorkerId", workerId.Value);
        var value = await command.ExecuteScalarAsync();
        return value is DateTimeOffset dateTimeOffset
            ? dateTimeOffset
            : throw new InvalidOperationException("Expected durable row to have a lease expiry.");
    }

    private static async Task WaitForEntryCount(SqlConnection connection, string subjectValue, int expected)
        => await TestEventually.Until(
            async () => await CountRowsForSubject(connection, subjectValue) == expected,
            $"Expected SQL Server work entry count for subject '{subjectValue}' to become {expected}.");

    private static async Task<object> StartWorkflow(IWorkSystem system, string workflowName)
    {
        var runtime = system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(system)
            ?? throw new InvalidOperationException("Expected workflow runtime property.");
        var startTask = (Task)(runtime.GetType()
            .GetMethod(
                "Start",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(WorkRequestContext), typeof(CancellationToken)],
                modifiers: null)?
            .Invoke(
                runtime,
                [workflowName, WorkRequestContext.Create(WorkInvocationChannel.InProcess), CancellationToken.None])
            ?? throw new InvalidOperationException("Expected workflow start task."));
        await startTask;
        return startTask.GetType().GetProperty("Result")?.GetValue(startTask)
            ?? throw new InvalidOperationException("Expected workflow start handle.");
    }

    private static async Task<object> ExecuteWorkflowAction(
        IWorkSystem system,
        WorkflowRunId runId,
        string actionName)
    {
        var runtime = system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(system)
            ?? throw new InvalidOperationException("Expected workflow runtime property.");
        var executeMethod = runtime.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
            {
                if (!string.Equals(method.Name, "Execute", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(WorkflowRunId) &&
                    parameters[2].ParameterType == typeof(WorkRequestContext) &&
                    parameters[3].ParameterType == typeof(CancellationToken);
            });
        var actionParameterType = executeMethod.GetParameters()[1].ParameterType;
        var action = Enum.Parse(actionParameterType, actionName, ignoreCase: false);
        var task = (Task)executeMethod.Invoke(
            runtime,
            [runId, action, WorkRequestContext.Create(WorkInvocationChannel.InProcess), CancellationToken.None])!;
        await task.WaitAsync(CancellationToken.None);
        return task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("Expected workflow action outcome.");
    }

    private static async Task<object> WaitForWorkflowCompletion(object handle)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waitTask = (Task)handle.GetType()
            .GetMethod("WaitForCompletion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(handle, [timeout.Token])!;
        await waitTask.WaitAsync(timeout.Token);
        return waitTask.GetType().GetProperty("Result")?.GetValue(waitTask)
            ?? throw new InvalidOperationException("Expected workflow completion result.");
    }

    private static bool IsWorkflowAccepted(object handle)
    {
        var startOutcome = handle.GetType().GetProperty("StartOutcome")?.GetValue(handle)
            ?? throw new InvalidOperationException("Expected workflow start outcome.");
        return (bool)(startOutcome.GetType().GetProperty("IsAccepted")?.GetValue(startOutcome) ?? false);
    }

    private static bool WorkflowActionAccepted(object outcome)
        => (bool)(outcome.GetType().GetProperty("IsAccepted")?.GetValue(outcome) ?? false);

    private static WorkflowRunId RequiredWorkflowRunId(object handle)
        => handle.GetType().GetProperty("RunId")?.GetValue(handle) is WorkflowRunId runId
            ? runId
            : throw new InvalidOperationException("Expected workflow run id.");

    private static string? WorkflowCompletionStatus(object completion)
        => completion.GetType().GetProperty("Status")?.GetValue(completion)?.ToString();

    private static string? WorkflowStatus(IWorkSystem system, WorkflowRunId runId)
    {
        var snapshot = WorkflowSnapshot(system, runId);
        return snapshot?.GetType().GetProperty("Status")?.GetValue(snapshot)?.ToString();
    }

    private static IReadOnlyList<WorkerId> WorkflowStepWorkerIds(
        IWorkSystem system,
        WorkflowRunId runId,
        string stepName)
    {
        var snapshot = WorkflowSnapshot(system, runId)
            ?? throw new InvalidOperationException("Expected workflow snapshot.");
        var steps = (System.Collections.IEnumerable)(snapshot.GetType().GetProperty("Steps")?.GetValue(snapshot)
            ?? throw new InvalidOperationException("Expected workflow steps."));
        var step = steps.Cast<object>().Single(candidate => string.Equals(
            candidate.GetType().GetProperty("Name")?.GetValue(candidate)?.ToString(),
            stepName,
            StringComparison.Ordinal));
        var workerIds = (System.Collections.IEnumerable)(step.GetType().GetProperty("WorkerIds")?.GetValue(step)
            ?? throw new InvalidOperationException("Expected workflow step worker ids."));
        return [.. workerIds.Cast<WorkerId>()];
    }

    private static object? WorkflowSnapshot(IWorkSystem system, WorkflowRunId runId)
    {
        var runtime = system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(system)
            ?? throw new InvalidOperationException("Expected workflow runtime property.");
        return runtime.GetType()
            .GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(runtime, [runId]);
    }

    private static WorkflowRunPersistenceRecord CreateWorkflowRun(
        string systemName,
        string definitionName)
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(definitionName);
        return new WorkflowRunPersistenceRecord(
            systemName,
            WorkflowRunId.New(),
            definition.Version,
            definitionName,
            WorkInput.FromValue(new WorkflowSqlInput("sql-input")),
            WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                new WorkActor("workflow-sql-user", "Workflow SQL User"),
                isAuthenticated: true),
            WorkflowRunStatus.Running,
            [
                new WorkflowStepPersistenceRecord(
                    "dispatch",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [workerId],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    []),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            [
                new WorkflowChildReceipt(
                    workerId,
                    "dispatch",
                    definitionName,
                    WorkerState.Completed,
                    DateTimeOffset.UtcNow,
                    [WorkMessage.Info("workflow.child.completed", "Child completed.")],
                    WorkOutput.Empty),
            ],
            "sql-test-workflow-fingerprint",
            "Stop",
            WorkRequestContext.Create(
                WorkInvocationChannel.HttpApi,
                new WorkActor("workflow-cancel-user", "Workflow Cancel User"),
                description: "Cancel for deployment",
                isAuthenticated: true));
    }

    private sealed record WorkflowSqlInput(string Value);

    private static WorkQueueDurabilityEnqueueRequest CreateDurableEnqueueRequest(
        WorkSystemId systemId,
        string systemName,
        WorkerId workerId,
        string definitionName,
        string subjectValue,
        IWorkQueueDurabilityTransaction? transaction,
        bool enableIdempotency = true,
        WorkerOptions? options = null)
        => new(
            systemId,
            systemName,
            workerId,
            WorkDefinition.Create(definitionName),
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue)),
            options ?? WorkerOptions.Default,
            DurablePersistenceConcurrencyConfiguration(enableIdempotency: enableIdempotency),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            enableIdempotency ? new WorkQueueDurabilityIdempotency(new WorkSubjectId("order", subjectValue)) : null,
            transaction);

    private static async Task WaitForFailedEntryRetained(SqlConnection connection, string subjectValue)
        => await TestEventually.Until(
            async () => await CountFailedRetainedRowsForSubject(connection, subjectValue) == 1,
            $"Expected failed SQL Server work entry for subject '{subjectValue}' to be retained.");

    private static IEnumerable<WorkProfileSnapshotNode> Flatten(WorkProfileSnapshotNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task ExpireLease(SqlConnection connection, string subjectValue)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
UPDATE queue
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
FROM workable.WorkQueueEntries queue
INNER JOIN workable.WorkEntries entries
    ON entries.WorkerId = queue.WorkerId
WHERE entries.SubjectValue = N'{Escape(subjectValue)}';
""";
        await command.ExecuteNonQueryAsync();
    }

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

    private static async Task WaitForWorkerState(IWorkSystem system, WorkerId workerId, WorkerState state)
        => await TestEventually.Until(
            async () => (await system.Query.Worker(workerId))?.State == state,
            $"Expected worker '{workerId.Value:D}' to reach state '{state}'.");

    private static Task<int> CountFailedRetainedRowsForSubject(SqlConnection connection, string subjectValue)
        => Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkEntries entries
LEFT JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.SubjectValue = N'{Escape(subjectValue)}'
  AND entries.IsDurableQueued = 0
  AND entries.LeaseId IS NULL
  AND entries.LeaseExpiresAt IS NULL
  AND queue.WorkerId IS NULL;
""");

    private static string Quote(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Escape(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Workable.SqlServer.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
