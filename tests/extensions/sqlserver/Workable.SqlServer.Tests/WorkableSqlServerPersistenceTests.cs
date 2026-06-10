using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
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
        await system.Stop();

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkEntries';
"""));
        Assert.Equal(4, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND columns.name IN (N'IsDurableQueued', N'HasIdempotencyReservation', N'ClaimedAt', N'ConcurrencyBucket');
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
FROM workable.WorkEntries
WHERE SubjectType = N'order'
  AND SubjectValue = N'combined'
  AND IsDurableQueued = 1
  AND HasIdempotencyReservation = 1;
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
FROM workable.WorkEntries
WHERE SubjectType = N'order'
  AND SubjectValue = N'durable-no-idempotency'
  AND IsDurableQueued = 1
  AND HasIdempotencyReservation = 0;
""");

        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(1, durableOnlyRows);
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
FROM workable.WorkEntries
WHERE ClaimedBy IN (N'consumer-one', N'consumer-two');
""");
        var firstRows = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE ClaimedBy = N'consumer-one';
""");
        var secondRows = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkEntries
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
FROM workable.WorkEntries
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
FROM workable.WorkEntries
WHERE WorkerId = '{firstWorkerId.Value}'
  AND IsDurableQueued = 0
  AND ConcurrencyBucket IS NULL;
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
        int batchSize)
    {
        var entries = new List<WorkQueueDurabilityEntry>();
        await foreach (var entry in store.ClaimReady(
            new WorkQueueDurabilityClaimRequest(
                WorkSystemName: null,
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
    @IsDurableQueued,
    @HasIdempotencyReservation,
    @SubjectType,
    @SubjectValue,
    @ConcurrencyType,
    @ConcurrencyValue,
    @InputJson,
    @OptionsJson,
    @ConfigurationJson,
    @OriginJson,
    @CreatedAt,
    @ClaimedBy,
    @LeaseId,
    @LeaseExpiresAt
);
""";
        command.Parameters.AddWithValue("@WorkerId", workerId.Value);
        command.Parameters.AddWithValue("@WorkSystemName", "default");
        command.Parameters.AddWithValue("@DefinitionName", definitionName);
        command.Parameters.AddWithValue("@IsDurableQueued", isDurableQueued);
        command.Parameters.AddWithValue("@HasIdempotencyReservation", hasIdempotencyReservation);
        command.Parameters.AddWithValue("@SubjectType", "order");
        command.Parameters.AddWithValue("@SubjectValue", subjectValue);
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
FROM workable.WorkEntries
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

    private static async Task WaitForFailedEntryRetained(SqlConnection connection, string subjectValue)
        => await TestEventually.Until(
            async () => await CountFailedRetainedRowsForSubject(connection, subjectValue) == 1,
            $"Expected failed SQL Server work entry for subject '{subjectValue}' to be retained.");

    private static async Task ExpireLease(SqlConnection connection, string subjectValue)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
UPDATE workable.WorkEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE SubjectValue = N'{Escape(subjectValue)}';
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
FROM workable.WorkEntries
WHERE SubjectValue = N'{Escape(subjectValue)}'
  AND IsDurableQueued = 0
  AND LeaseId IS NULL
  AND LeaseExpiresAt IS NULL;
""");

    private static string Quote(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Escape(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
