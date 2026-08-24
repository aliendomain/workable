using Workable.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkableSqlServerProfilingPerformanceShould
{
    [Fact]
    public void ShareOneDiagnosticObserverAcrossSystemsAndReferenceCountLeases()
    {
        var firstSystem = WorkSystemId.New();
        var secondSystem = WorkSystemId.New();
        var accessor = new EmptyProfilingContextAccessor();
        using var factory = new WorkableSqlServerProfilingInstrumentationFactory();
        var first = factory.Create(firstSystem, accessor);
        var observer = factory.Observer;
        var duplicate = factory.Create(firstSystem, accessor);
        var second = factory.Create(secondSystem, accessor);

        Assert.NotNull(observer);
        Assert.Same(observer, factory.Observer);
        first.Dispose();
        Assert.Same(observer, factory.Observer);
        duplicate.Dispose();
        Assert.Same(observer, factory.Observer);
        second.Dispose();
        Assert.Null(factory.Observer);
    }

    [Fact]
    public void RejectDifferentProfilingAccessorsAndCreationAfterDisposal()
    {
        var systemId = WorkSystemId.New();
        var firstAccessor = new EmptyProfilingContextAccessor();
        var secondAccessor = new EmptyProfilingContextAccessor();
        var factory = new WorkableSqlServerProfilingInstrumentationFactory();
        var registration = factory.Create(systemId, firstAccessor);

        var mismatch = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(WorkSystemId.New(), secondAccessor));
        Assert.Contains("share one profiling context accessor", mismatch.Message);

        factory.Dispose();
        factory.Dispose();
        registration.Dispose();
        registration.Dispose();

        Assert.Throws<ObjectDisposedException>(() => factory.Create(systemId, firstAccessor));
        Assert.Throws<ArgumentNullException>(() => factory.Create(systemId, null!));
    }

    [Fact]
    public void ObserverLifecycleIsIdempotentAndIgnoresUnrelatedOrLateEvents()
    {
        var systemId = WorkSystemId.New();
        var observer = new WorkableSqlServerCommandProfilingObserver(
            systemId,
            new EmptyProfilingContextAccessor());
        using var unrelated = new DiagnosticListener("Application.Diagnostics");
        using var sql = new DiagnosticListener("Microsoft.Data.SqlClient.CoverageTests");

        observer.OnNext(unrelated);
        observer.OnNext(sql);
        observer.OnNext(sql);
        observer.OnNext(new KeyValuePair<string, object?>("unknown", null));
        observer.OnCompleted();
        observer.OnError(new InvalidOperationException("listener failure"));
        observer.UnregisterSystem(WorkSystemId.New());
        observer.UnregisterSystem(systemId);
        observer.UnregisterSystem(systemId);
        observer.RegisterSystem(systemId);
        observer.Dispose();
        observer.Dispose();
        observer.OnNext(sql);
        observer.OnNext(new KeyValuePair<string, object?>("late", null));

        Assert.False(sql.IsEnabled(SqlClientCommandBefore.Name));
    }

    [Fact]
    public void EnableSqlEventsOnlyWhileARegisteredWorkableProfileCouldConsumeThem()
    {
        var systemId = WorkSystemId.New();
        var accessor = new MutableProfilingContextAccessor();
        using var listener = new DiagnosticListener("Microsoft.Data.SqlClient.ProfilingPerformanceTests");
        using var observer = new WorkableSqlServerCommandProfilingObserver(systemId, accessor);
        observer.OnNext(listener);

        Assert.False(listener.IsEnabled(SqlClientCommandBefore.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandAfter.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandError.Name));

        accessor.Current = new WorkProfilingContext(systemId, null!);

        Assert.True(listener.IsEnabled(SqlClientCommandBefore.Name));
        Assert.True(listener.IsEnabled(SqlClientCommandAfter.Name));
        Assert.True(listener.IsEnabled(SqlClientCommandError.Name));

        accessor.Current = null;

        Assert.False(listener.IsEnabled(SqlClientCommandBefore.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandAfter.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandError.Name));
    }

    [Fact]
    public void CaptureUnsupportedParameterWithoutCallingApplicationToString()
    {
        using var command = new SqlCommand { CommandText = new string(' ', 100_000) };
        command.Parameters.Add(new SqlParameter("@Custom", SqlDbType.Variant)
        {
            Value = new ThrowingStringValue(),
        });

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command));

        Assert.Contains("\"Statement\":\"\\u003Cempty\\u003E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"StatementKind\":\"UNKNOWN\"", json, StringComparison.Ordinal);
        Assert.Contains("\\u003CThrowingStringValue\\u003E", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkByteArrayValuesAsOmittedWhenTheirDeclaredSqlTypeIsVariant()
    {
        using var command = new SqlCommand { CommandText = "SELECT @Payload;" };
        command.Parameters.Add(new SqlParameter("@Payload", SqlDbType.Variant)
        {
            Value = new byte[] { 0x01, 0x02, 0x03 },
        });

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command));

        Assert.Contains("\"Type\":\"Variant\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Value\":\"\\u003Cbinary omitted\\u003E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"IsBinaryOmitted\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFailureDetailsToTheTimingContextWithoutRecapturingTheCommand()
    {
        using var command = new SqlCommand { CommandText = "SELECT @Value;" };
        command.Parameters.AddWithValue("@Value", 42);
        var exception = new InvalidOperationException(new string('e', 8_000));

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureFailedCommandForBenchmark(command, exception));

        Assert.Contains("\"Outcome\":\"Faulted\"", json, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", json, StringComparison.Ordinal);
        Assert.Contains("\"MessageTruncated\":true", json, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(json, "\"Statement\""));
        Assert.Equal(1, CountOccurrences(json, "\"Parameters\""));
    }

    [Fact]
    public void BoundAndRedactUncorrelatedSqlFailureContext()
    {
        using var command = new SqlCommand { CommandText = "SELECT @Password;" };
        command.Parameters.AddWithValue("@Password", "must-not-appear");
        var exception = new InvalidOperationException(new string('e', 8_000));

        var whitespaceJson = JsonSerializer.Serialize(InvokePrivateStatic(
            "CreateFailureContext",
            "   ",
            command,
            exception));
        var nullOperationJson = JsonSerializer.Serialize(InvokePrivateStatic(
            "CreateFailureContext",
            null,
            command,
            exception));
        var longOperationJson = JsonSerializer.Serialize(InvokePrivateStatic(
            "CreateFailureContext",
            new string('o', 1_000),
            command,
            exception));

        Assert.Contains("\"Operation\":\"Command\"", whitespaceJson, StringComparison.Ordinal);
        Assert.Contains("\"Operation\":\"Command\"", nullOperationJson, StringComparison.Ordinal);
        Assert.Contains("\"MessageTruncated\":true", whitespaceJson, StringComparison.Ordinal);
        Assert.Contains("\\u003Credacted\\u003E", whitespaceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", whitespaceJson, StringComparison.Ordinal);
        using var longOperation = JsonDocument.Parse(longOperationJson);
        Assert.Equal(512, longOperation.RootElement.GetProperty("Operation").GetString()!.Length);
    }

    [Fact]
    public void RedactParameterNamesBeyondTheDiagnosticMetadataLimit()
    {
        Assert.True(InvokePrivateStatic<bool>("ShouldRedactParameter", new string('n', 513)));
        Assert.False(InvokePrivateStatic<bool>("ShouldRedactParameter", "@OrdinaryValue"));
        Assert.NotNull(InvokePrivateStatic("CaptureParameterValue", null, 10));
    }

    [Fact]
    public void BoundAndClassifyDiverseCommandParameterValues()
    {
        using var connection = new SqlConnection(
            "Server=localhost;Initial Catalog=profile_database;Integrated Security=true;TrustServerCertificate=true");
        using var command = connection.CreateCommand();
        command.CommandText = new string('S', 9_000);
        command.Parameters.Add(new SqlParameter("", SqlDbType.NVarChar) { Value = DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Chars", SqlDbType.NVarChar) { Value = new[] { 'a', 'b' } });
        command.Parameters.Add(new SqlParameter("@Character", SqlDbType.NVarChar) { Value = 'c' });
        command.Parameters.Add(new SqlParameter("@Boolean", SqlDbType.Bit) { Value = true });
        command.Parameters.Add(new SqlParameter("@Number", SqlDbType.Int) { Value = 42 });
        command.Parameters.Add(new SqlParameter("@Guid", SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
        command.Parameters.Add(new SqlParameter("@Timestamp", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.UtcNow });
        command.Parameters.Add(new SqlParameter("@Enum", SqlDbType.Variant) { Value = DayOfWeek.Monday });
        command.Parameters.Add(new SqlParameter("@Enumerable", SqlDbType.Variant) { Value = new[] { 1, 2 } });
        command.Parameters.Add(new SqlParameter("@Api-Key", SqlDbType.NVarChar) { Value = "must-not-appear" });
        command.Parameters.Add(new SqlParameter("@SharedAccessSignature", SqlDbType.NVarChar) { Value = "also-secret" });
        for (var index = command.Parameters.Count; index < 40; index++)
        {
            command.Parameters.Add(new SqlParameter($"@Value{index}", SqlDbType.NVarChar)
            {
                Value = new string('v', 400),
            });
        }

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command));

        Assert.Contains("\"StatementTruncated\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"ParameterCount\":40", json, StringComparison.Ordinal);
        Assert.Contains("\"ParametersTruncated\":true", json, StringComparison.Ordinal);
        Assert.Contains("profile_database", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003Credacted\\u003E", json, StringComparison.Ordinal);
        Assert.Contains("\\u003CInt32[]\\u003E", json, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-appear", json, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("select;")]
    [InlineData("  update WorkItems set Value = 1")]
    public void NormalizeSparseAndDelimitedSqlStatements(string commandText)
    {
        using var command = new SqlCommand { CommandText = commandText };

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command));

        if (string.IsNullOrWhiteSpace(commandText))
        {
            Assert.Contains("\"Statement\":\"\\u003Cempty\\u003E\"", json, StringComparison.Ordinal);
            Assert.Contains("\"StatementKind\":\"UNKNOWN\"", json, StringComparison.Ordinal);
        }
        else
        {
            var expectedKind = commandText.Contains("update", StringComparison.OrdinalIgnoreCase)
                ? "UPDATE"
                : "SELECT";
            Assert.Contains($"\"StatementKind\":\"{expectedKind}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BoundCapturedParametersByCountAsWellAsSerializedSize()
    {
        using var command = new SqlCommand { CommandText = "SELECT 1;" };
        for (var index = 0; index < 40; index++)
        {
            command.Parameters.AddWithValue($"@P{index}", index);
        }

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command)));

        Assert.Equal(40, json.RootElement.GetProperty("ParameterCount").GetInt32());
        Assert.Equal(32, json.RootElement.GetProperty("CapturedParameterCount").GetInt32());
        Assert.True(json.RootElement.GetProperty("ParametersTruncated").GetBoolean());
    }

    [Fact]
    public void CompleteFailAndFinalizeCorrelatedDiagnosticCommands()
    {
        var systemId = WorkSystemId.New();
        var profile = CreateWorkProfile();
        var accessor = new MutableProfilingContextAccessor
        {
            Current = new WorkProfilingContext(systemId, profile),
        };
        using var observer = new WorkableSqlServerCommandProfilingObserver(systemId, accessor);
        using var command = new SqlCommand { CommandText = "SELECT 1;" };

        var completedId = Guid.NewGuid();
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(completedId, "ExecuteReader", command)));
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandAfter.Name,
            CreateDiagnostic<SqlClientCommandAfter>(completedId, "ExecuteReader", command)));

        var failedId = Guid.NewGuid();
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(failedId, "ExecuteScalar", command)));
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandError.Name,
            CreateDiagnostic<SqlClientCommandError>(
                failedId,
                "ExecuteScalar",
                command,
                new InvalidOperationException("database failure"))));

        var incompleteId = Guid.NewGuid();
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(incompleteId, "ExecuteNonQuery", command)));
        observer.UnregisterSystem(systemId);

        var snapshot = SerializeWorkProfile(profile);
        Assert.Contains("Completed", snapshot, StringComparison.Ordinal);
        Assert.Contains("Faulted", snapshot, StringComparison.Ordinal);
        Assert.Contains("Incomplete", snapshot, StringComparison.Ordinal);
        Assert.Contains("database failure", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceDuplicateCommandAndRecordUncorrelatedFailure()
    {
        var systemId = WorkSystemId.New();
        var profile = CreateWorkProfile();
        var accessor = new MutableProfilingContextAccessor
        {
            Current = new WorkProfilingContext(systemId, profile),
        };
        using var observer = new WorkableSqlServerCommandProfilingObserver(systemId, accessor);
        using var command = new SqlCommand { CommandText = "UPDATE Queue SET State = 1;" };
        var operationId = Guid.NewGuid();
        var before = CreateDiagnostic<SqlClientCommandBefore>(operationId, "ExecuteNonQuery", command);

        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name, before));
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name, before));
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandAfter.Name,
            CreateDiagnostic<SqlClientCommandAfter>(operationId, "ExecuteNonQuery", command)));
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandAfter.Name,
            CreateDiagnostic<SqlClientCommandAfter>(Guid.NewGuid(), "ExecuteNonQuery", command)));
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandError.Name,
            CreateDiagnostic<SqlClientCommandError>(
                Guid.NewGuid(),
                "ExecuteNonQuery",
                command,
                new InvalidOperationException("uncorrelated failure"))));

        var snapshot = SerializeWorkProfile(profile);
        Assert.Contains("Incomplete", snapshot, StringComparison.Ordinal);
        Assert.Contains("Completed", snapshot, StringComparison.Ordinal);
        Assert.Contains("SQL Error", snapshot, StringComparison.Ordinal);
        Assert.Contains("uncorrelated failure", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizeActiveCommandsOnDisposeAndRejectWrongOrFinalizedProfiles()
    {
        var registeredSystem = WorkSystemId.New();
        var profile = CreateWorkProfile();
        var accessor = new MutableProfilingContextAccessor
        {
            Current = new WorkProfilingContext(WorkSystemId.New(), profile),
        };
        using var listener = new DiagnosticListener("Microsoft.Data.SqlClient.ObserverBranches");
        var observer = new WorkableSqlServerCommandProfilingObserver(registeredSystem, accessor);
        observer.OnNext(listener);
        Assert.False(listener.IsEnabled(SqlClientCommandBefore.Name));

        accessor.Current = new WorkProfilingContext(registeredSystem, profile);
        SerializeWorkProfile(profile);
        using var command = new SqlCommand { CommandText = "SELECT 1;" };
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(Guid.NewGuid(), "ExecuteReader", command)));

        var activeProfile = CreateWorkProfile();
        accessor.Current = new WorkProfilingContext(registeredSystem, activeProfile);
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(Guid.NewGuid(), "ExecuteReader", command)));
        observer.Dispose();

        Assert.Contains("Incomplete", SerializeWorkProfile(activeProfile), StringComparison.Ordinal);
    }

    [Fact]
    public void SupportPlainProfilersAndMakeActiveCompletionIdempotent()
    {
        var systemId = WorkSystemId.New();
        var profile = new PlainProfiler();
        var accessor = new MutableProfilingContextAccessor
        {
            Current = new WorkProfilingContext(systemId, profile),
        };
        using var observer = new WorkableSqlServerCommandProfilingObserver(systemId, accessor);
        using var command = new SqlCommand { CommandText = "SELECT @Value;" };
        command.Parameters.AddWithValue(" ", 1);
        var operationId = Guid.NewGuid();

        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(operationId, "ExecuteReader", command)));
        var activeCommands = (System.Collections.IEnumerable)typeof(WorkableSqlServerCommandProfilingObserver)
            .GetField("activeCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(observer)!;
        var entry = Assert.Single(activeCommands.Cast<object>());
        var active = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        var activeType = active.GetType();
        activeType.GetMethod("Complete")!.Invoke(active, null);
        activeType.GetMethod("RemovePendingRegistrationIfCompleted")!.Invoke(active, null);
        activeType.GetMethod("Complete")!.Invoke(active, null);
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandAfter.Name,
            CreateDiagnostic<SqlClientCommandAfter>(operationId, "ExecuteReader", command)));
        activeType.GetMethod("FinalizeForProfileSnapshot")!.Invoke(active, null);

        Assert.NotNull(profile.LastContext);
        Assert.True(profile.Scope.Disposed);
    }

    [Fact]
    public void UnregisterSystemToleratesACommandAlreadyRemovedFromTheGlobalIndex()
    {
        var systemId = WorkSystemId.New();
        var profile = new PlainProfiler();
        var accessor = new MutableProfilingContextAccessor
        {
            Current = new WorkProfilingContext(systemId, profile),
        };
        using var observer = new WorkableSqlServerCommandProfilingObserver(systemId, accessor);
        using var command = new SqlCommand { CommandText = "SELECT 1;" };
        var operationId = Guid.NewGuid();
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(operationId, "ExecuteReader", command)));
        var activeCommands = typeof(WorkableSqlServerCommandProfilingObserver)
            .GetField("activeCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(observer)!;
        var activeType = activeCommands.GetType().GetGenericArguments()[1];
        var tryRemove = activeCommands.GetType().GetMethod(
            "TryRemove",
            [typeof(Guid), activeType.MakeByRefType()])!;
        var arguments = new object?[] { operationId, null };

        Assert.True(Assert.IsType<bool>(tryRemove.Invoke(activeCommands, arguments)));
        observer.UnregisterSystem(systemId);
        arguments[1]!.GetType().GetMethod("FinalizeIncomplete")!.Invoke(arguments[1], null);

        Assert.True(profile.Scope.Disposed);
    }

    [Fact]
    public async Task SerializeConcurrentActiveCompletionAndFactoryReregistration()
    {
        var systemId = WorkSystemId.New();
        var profile = new BlockingProfiler();
        var accessor = new MutableProfilingContextAccessor
        {
            Current = new WorkProfilingContext(systemId, profile),
        };
        using var factory = new WorkableSqlServerProfilingInstrumentationFactory();
        var registration = factory.Create(systemId, accessor);
        var observer = Assert.IsType<WorkableSqlServerCommandProfilingObserver>(factory.Observer);
        using var command = new SqlCommand { CommandText = "SELECT 1;" };
        var operationId = Guid.NewGuid();
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(operationId, "ExecuteReader", command)));

        var activeCommands = (System.Collections.IEnumerable)typeof(WorkableSqlServerCommandProfilingObserver)
            .GetField("activeCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(observer)!;
        var entry = Assert.Single(activeCommands.Cast<object>());
        var active = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        var complete = active.GetType().GetMethod("Complete")!;
        var firstCompletion = Task.Run(() => complete.Invoke(active, null));
        Assert.True(profile.Scope.Entered.Wait(TimeSpan.FromSeconds(2)));
        var secondCompletion = Task.Run(() => complete.Invoke(active, null));
        await Task.Delay(25);
        Assert.False(secondCompletion.IsCompleted);
        profile.Scope.Release.Set();
        await Task.WhenAll(firstCompletion, secondCompletion);

        var secondProfile = new BlockingProfiler();
        accessor.Current = new WorkProfilingContext(systemId, secondProfile);
        var secondOperationId = Guid.NewGuid();
        observer.OnNext(new KeyValuePair<string, object?>(SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(secondOperationId, "ExecuteReader", command)));
        var stopping = Task.Run(registration.Dispose);
        Assert.True(secondProfile.Scope.Entered.Wait(TimeSpan.FromSeconds(2)));
        var reregister = Task.Run(() => factory.Create(systemId, accessor));
        await Task.Delay(25);
        Assert.False(reregister.IsCompleted);
        secondProfile.Scope.Release.Set();
        await stopping;
        using var replacement = await reregister;
    }

    [Fact]
    public void CoverStaticCaptureBoundaries()
    {
        foreach (var name in new[]
        {
            "@pwd", "@client_secret", "@access_token", "@refresh-token", "@bearerToken",
            "@api_key", "@access-key", "@private_key", "@shared_access_signature",
        })
        {
            Assert.True(InvokePrivateStatic<bool>("ShouldRedactParameter", name));
        }

        Assert.Equal(string.Empty, InvokePrivateStatic<string>("InferStatementKind", ";,,"));
        Assert.NotNull(InvokePrivateStatic("CaptureParameterValue", new byte[] { 1, 2 }, 10));
        Assert.NotNull(InvokePrivateStatic("CaptureParameterValue", "abcdef", -1));
        Assert.NotNull(InvokePrivateStatic("CaptureParameterValue", new[] { 'a', 'b', 'c' }, 1));
        Assert.Null(InvokePrivateStatic<string?>("TruncateNullable", null, 10));
        Assert.Equal("abc", InvokePrivateStatic<string?>("TruncateNullable", "abc", 10));

        var shortParameter = new SqlParameter("@Value", "ok");
        var longParameter = new SqlParameter("@Value", new string('v', 100));
        var shortContext = InvokePrivateStatic("CreateParameter", shortParameter, 100);
        var longContext = InvokePrivateStatic("CreateParameter", longParameter, 4);
        Assert.Contains("\"IsTruncated\":false", JsonSerializer.Serialize(shortContext), StringComparison.Ordinal);
        Assert.Contains("\"IsTruncated\":true", JsonSerializer.Serialize(longContext), StringComparison.Ordinal);

        using var command = new SqlCommand { CommandText = "SELECT parameters;" };
        var observer = new WorkableSqlServerCommandProfilingObserver(
            WorkSystemId.New(),
            new EmptyProfilingContextAccessor());
        observer.OnNext(new KeyValuePair<string, object?>(
            SqlClientCommandBefore.Name,
            CreateDiagnostic<SqlClientCommandBefore>(Guid.NewGuid(), "ExecuteReader", command)));
        observer.Dispose();

        for (var index = 0; index < 6; index++)
        {
            command.Parameters.AddWithValue($"@Large{index}", new string('x', 4_096));
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command)));
        Assert.True(document.RootElement.GetProperty("ParametersTruncated").GetBoolean());
        Assert.InRange(document.RootElement.GetProperty("CapturedParameterCount").GetInt32(), 1, 5);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(search, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += search.Length;
        }

        return count;
    }

    private static object InvokePrivateStatic(string methodName, params object?[] arguments)
        => typeof(WorkableSqlServerCommandProfilingObserver)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments)!;

    private static T InvokePrivateStatic<T>(string methodName, params object?[] arguments)
        => (T)InvokePrivateStatic(methodName, arguments);

    private static T CreateDiagnostic<T>(
        Guid operationId,
        string operation,
        SqlCommand command,
        Exception? exception = null)
    {
        var constructor = Assert.Single(typeof(T).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.Name switch
            {
                "operationId" => (object?)operationId,
                "operation" => operation,
                "timestamp" => 0L,
                "connectionId" => null,
                "transactionId" => null,
                "command" => command,
                "exception" => exception,
                _ => parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null,
            })
            .ToArray();
        return (T)constructor.Invoke(arguments);
    }

    private static IWorkProfiler CreateWorkProfile()
    {
        var workableAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => assembly.GetName().Name == "Workable");
        var profileType = workableAssembly.GetType("Workable.WorkProfile", throwOnError: true)!;
        return (IWorkProfiler)Activator.CreateInstance(
            profileType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["sql diagnostics", 256, WorkProfileCaptureMode.Bounded],
            culture: null)!;
    }

    private static string SerializeWorkProfile(IWorkProfiler profile)
    {
        var snapshot = profile.GetType()
            .GetMethod("ToSnapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(profile, null);
        return JsonSerializer.Serialize(snapshot, snapshot!.GetType());
    }

    private sealed class ThrowingStringValue
    {
        public override string ToString() => throw new InvalidOperationException("ToString must not be called.");
    }

    private sealed class EmptyProfilingContextAccessor : IWorkProfilingContextAccessor
    {
        public bool TryGetCurrent(out WorkProfilingContext context)
        {
            context = default;
            return false;
        }
    }

    private sealed class MutableProfilingContextAccessor : IWorkProfilingContextAccessor
    {
        public WorkProfilingContext? Current { get; set; }

        public bool TryGetCurrent(out WorkProfilingContext context)
        {
            if (this.Current is { } current)
            {
                context = current;
                return true;
            }

            context = default;
            return false;
        }
    }

    private sealed class PlainProfiler : IWorkProfiler
    {
        public PlainScope Scope { get; } = new();

        public object? LastContext { get; private set; }

        public void AddInfo(string name, object? context = null) => this.LastContext = context;

        public IWorkProfileScope StartTiming(string name, object? context = null)
        {
            this.LastContext = context;
            return this.Scope;
        }

        public IWorkProfileScope CreateScope(string name, object? context = null)
            => this.StartTiming(name, context);

        public IWorkProfileScope CreateMethodScope(
            Type type,
            string methodName,
            object? context = null,
            string label = "Input")
            => this.StartTiming(methodName, context);

        public IWorkProfileScope CreateMethodScope<T>(
            object? context = null,
            string label = "Input",
            [System.Runtime.CompilerServices.CallerMemberName] string methodName = "")
            => this.StartTiming(methodName, context);
    }

    private sealed class PlainScope : IWorkProfileScope
    {
        public bool Disposed { get; private set; }

        public void SetResult(object? context = null)
        {
        }

        public void Dispose() => this.Disposed = true;
    }

    private sealed class BlockingProfiler : IWorkProfiler
    {
        public BlockingScope Scope { get; } = new();

        public void AddInfo(string name, object? context = null)
        {
        }

        public IWorkProfileScope StartTiming(string name, object? context = null) => this.Scope;

        public IWorkProfileScope CreateScope(string name, object? context = null) => this.Scope;

        public IWorkProfileScope CreateMethodScope(
            Type type,
            string methodName,
            object? context = null,
            string label = "Input")
            => this.Scope;

        public IWorkProfileScope CreateMethodScope<T>(
            object? context = null,
            string label = "Input",
            [System.Runtime.CompilerServices.CallerMemberName] string methodName = "")
            => this.Scope;
    }

    private sealed class BlockingScope : IWorkProfileScope
    {
        public ManualResetEventSlim Entered { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public void SetResult(object? context = null)
        {
        }

        public void Dispose()
        {
            this.Entered.Set();
            Assert.True(this.Release.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}
