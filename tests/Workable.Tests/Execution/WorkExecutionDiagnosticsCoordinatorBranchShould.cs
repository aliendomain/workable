using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "ExecutionDiagnostics")]
public sealed class WorkExecutionDiagnosticsCoordinatorBranchShould
{
    [Fact]
    public async Task TemporaryRulesDrivePerIterationLogAndProfileAdmission()
    {
        var repository = new TestExecutionDiagnosticsRepository();
        var configuration = new WorkSystemExecutionDiagnosticsPersistenceConfiguration
        {
            IsEnabled = true,
            MinimumLogLevel = LogLevel.Warning,
            ChannelCapacity = 32,
            ControlOperationCapacity = 8,
        };
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            "diagnostics-branches",
            repository,
            configuration,
            logger: null,
            isProduction: true);
        var worker = CreateRunningWorker("diagnostics.rules");
        var iteration = Assert.IsType<WorkerIterationSnapshot>(worker.ToSnapshot().CurrentIteration);

        // A registered repository is deliberately inert until initialization completes.
        coordinator.ObserveIteration(worker, iteration);
        Assert.False(coordinator.IsLogEnabled(worker, LogLevel.Warning));
        Assert.False(coordinator.TryCaptureProfile(worker, new WorkProfile("not-admitted")));

        await coordinator.Initialize([worker.Work.Definition], CancellationToken.None);
        var systemRule = await coordinator.CreateCaptureRule(
            definitionName: null,
            LogLevel.Error,
            WorkProfileCaptureMode.Bounded,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            new WorkActor("tester"),
            CancellationToken.None);
        Assert.Equal(
            WorkExecutionDiagnosticCaptureSource.TemporarySystemRule,
            coordinator.ResolvePolicy(worker.Configuration, worker.Work.Definition.Name)?.CaptureSource);

        var workRule = await coordinator.CreateCaptureRule(
            worker.Work.Definition.Name,
            LogLevel.Warning,
            WorkProfileCaptureMode.Full,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            new WorkActor("tester"),
            CancellationToken.None);
        Assert.Equal(
            WorkExecutionDiagnosticCaptureSource.TemporaryWorkRule,
            coordinator.ResolvePolicy(worker.Configuration, worker.Work.Definition.Name)?.CaptureSource);
        Assert.Contains(systemRule, coordinator.GetCaptureRules());
        Assert.Contains(workRule, coordinator.GetCaptureRules());

        coordinator.ObserveIteration(worker, iteration);
        coordinator.ObserveIteration(worker, iteration); // duplicate execution observation is idempotent
        Assert.False(coordinator.IsLogEnabled(worker, LogLevel.None));
        Assert.False(coordinator.IsLogEnabled(worker, LogLevel.Information));
        Assert.True(coordinator.IsLogEnabled(worker, LogLevel.Warning));
        Assert.Equal(WorkProfileCaptureMode.Full, coordinator.ResolveProfileCaptureMode(worker));

        coordinator.CaptureLog(
            worker,
            DateTimeOffset.UtcNow,
            "diagnostics.rules",
            LogLevel.Information,
            new EventId(1, "below-threshold"),
            retainedEntry: null,
            logState: "ignored",
            exception: null,
            formatter: static (state, _) => state,
            traceId: null,
            spanId: null);
        Exception exception;
        try
        {
            throw new InvalidOperationException("captured failure");
        }
        catch (Exception captured)
        {
            exception = captured;
        }
        coordinator.CaptureLog(
            worker,
            DateTimeOffset.UtcNow,
            "diagnostics.rules",
            LogLevel.Error,
            new EventId(2, "captured"),
            retainedEntry: null,
            logState: new[]
            {
                new KeyValuePair<string, object?>("count", 2),
                new KeyValuePair<string, object?>("optional", null),
                new KeyValuePair<string, object?>("{OriginalFormat}", "ignored"),
            },
            exception,
            formatter: static (_, error) => error!.Message,
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom());

        var profile = new WorkProfile("captured");
        profile.AddInfo("evidence");
        Assert.True(coordinator.TryCaptureProfile(worker, profile));

        worker.Complete(WorkExecutionResult.Success());
        coordinator.ObserveIteration(worker, Assert.Single(worker.ToSnapshot().Iterations));
        await coordinator.Flush();
        Assert.True(await coordinator.DeleteCaptureRule(workRule.Id, CancellationToken.None));
        Assert.False(await coordinator.DeleteCaptureRule(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task UnavailableCoordinatorReturnsEmptyReadsAndRejectsMutations()
    {
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            null,
            repository: null,
            WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default,
            logger: null);

        Assert.False(coordinator.IsAvailable);
        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.NotConfigured,
            coordinator.PersistenceDiagnostics.Status);
        Assert.Empty((await coordinator.Query(
            new WorkExecutionDiagnosticCriteria(WorkSystemId.New()),
            CancellationToken.None)).Items);
        Assert.Null(await coordinator.Get(
            new WorkExecutionDiagnosticGetRequest(WorkSystemId.New(), WorkerId.New(), 1),
            CancellationToken.None));
        Assert.False(await coordinator.DeleteCaptureRule(Guid.NewGuid(), CancellationToken.None));
        await coordinator.Flush();
        await coordinator.Initialize([], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateCaptureRule(
            null,
            LogLevel.Information,
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            new WorkActor("tester"),
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.Initialize(
            [WorkDefinition.Create("diagnosed", configuration: WorkConfiguration.Default with
            {
                ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
                {
                    IsEnabled = true,
                },
            })],
            CancellationToken.None));
    }

    [Fact]
    public async Task StoreUnavailableDuringInitializationDisablesDiagnosticsWithoutFailingTheSystem()
    {
        var unavailable = new WorkPersistenceStoreUnavailableException(
            "Diagnostics store unavailable.",
            new InvalidOperationException("Simulated connection failure."));
        var repository = new TestExecutionDiagnosticsRepository
        {
            InitializeException = unavailable,
        };
        var logger = new RecordingLogger();
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            "diagnostics-unavailable",
            repository,
            new WorkSystemExecutionDiagnosticsPersistenceConfiguration { IsEnabled = true },
            logger);

        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.PendingInitialization,
            coordinator.PersistenceDiagnostics.Status);
        var initializationStartedAt = DateTimeOffset.UtcNow;

        await coordinator.Initialize([], CancellationToken.None);
        await coordinator.Initialize([], CancellationToken.None);

        Assert.False(coordinator.IsAvailable);
        Assert.False(coordinator.PersistenceDiagnostics.IsHealthy);
        Assert.False(coordinator.PersistenceDiagnostics.PersistenceAvailable);
        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.Unhealthy,
            coordinator.PersistenceDiagnostics.Status);
        Assert.InRange(
            coordinator.PersistenceDiagnostics.InitializationFailedAt!.Value,
            initializationStartedAt.AddMilliseconds(-1),
            DateTimeOffset.UtcNow);
        Assert.Equal(1, repository.InitializeCallCount);
        Assert.Empty((await coordinator.Query(
            new WorkExecutionDiagnosticCriteria(WorkSystemId.New()),
            CancellationToken.None)).Items);
        Assert.Null(await coordinator.Get(
            new WorkExecutionDiagnosticGetRequest(WorkSystemId.New(), WorkerId.New(), 1),
            CancellationToken.None));
        Assert.False(await coordinator.DeleteCaptureRule(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateCaptureRule(
            null,
            LogLevel.Information,
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            new WorkActor("tester"),
            CancellationToken.None));
        var error = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Equal("ExecutionDiagnosticsInitializationFailed", error.EventId.Name);
        Assert.Same(unavailable, error.Exception);
        Assert.Contains("UNHEALTHY", error.Message, StringComparison.Ordinal);
        Assert.Contains("WILL NOT BE PERSISTED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderInitializationFailureDisablesDiagnosticsWithoutFailingStartup()
    {
        var failure = new InvalidOperationException("Invalid diagnostics schema.");
        var repository = new TestExecutionDiagnosticsRepository
        {
            InitializeException = failure,
        };
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            "diagnostics-invalid",
            repository,
            WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default,
            logger: null);

        await coordinator.Initialize([], CancellationToken.None);

        Assert.False(coordinator.IsAvailable);
        Assert.Equal(1, repository.InitializeCallCount);
    }

    [Fact]
    public async Task CancellationDuringInitializationStillPropagates()
    {
        var repository = new TestExecutionDiagnosticsRepository
        {
            InitializeException = new OperationCanceledException("Host startup was canceled."),
        };
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            "diagnostics-canceled",
            repository,
            WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default,
            logger: null);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.Initialize([], CancellationToken.None));

        Assert.True(coordinator.IsAvailable);
        Assert.Equal(
            WorkExecutionDiagnosticsPersistenceHealthStatus.PendingInitialization,
            coordinator.PersistenceDiagnostics.Status);
        Assert.Equal(1, repository.InitializeCallCount);
    }

    [Fact]
    public async Task CaptureRuleLoadFailureDisablesDiagnosticsWithoutFailingStartup()
    {
        var repository = new TestExecutionDiagnosticsRepository
        {
            ListCaptureRulesException = new InvalidOperationException("Capture rules could not be loaded."),
        };
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            "diagnostics-rules-unavailable",
            repository,
            WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default,
            logger: null);

        await coordinator.Initialize([], CancellationToken.None);

        Assert.False(coordinator.IsAvailable);
        Assert.Equal(1, repository.InitializeCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task QueryRejectsEveryOutOfRangeTakeBoundary(int take)
    {
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            null,
            repository: null,
            WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default,
            logger: null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.Query(
            new WorkExecutionDiagnosticCriteria(WorkSystemId.New(), Take: take),
            CancellationToken.None));
    }

    [Fact]
    public void StructuredLogPropertyCaptureBoundsTypesCountsAndText()
    {
        Assert.Empty(InvokeStatic<IReadOnlyList<KeyValuePair<string, object?>>>(
            "CaptureProperties",
            new object(),
            20));

        var properties = new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", "ignored"),
            new("null", null),
            new("number", 42),
            new("text", "abcdefghijklmnopqrstuvwxyz"),
        };
        var captured = InvokeStatic<IReadOnlyList<KeyValuePair<string, object?>>>(
            "CaptureProperties",
            properties,
            18);

        Assert.DoesNotContain(captured, item => item.Key == "{OriginalFormat}");
        Assert.Contains(captured, item => item.Key == "null" && item.Value is null);
        Assert.Contains(captured, item => item.Key == "number" && Equals(item.Value, 42));
        Assert.Contains(captured, item => item.Key == "workablePropertiesTruncated" && Equals(item.Value, true));

        var tooMany = Enumerable.Range(0, 70)
            .Select(index => new KeyValuePair<string, object?>($"k{index}", true))
            .ToArray();
        var countBounded = InvokeStatic<IReadOnlyList<KeyValuePair<string, object?>>>(
            "CaptureProperties",
            tooMany,
            10_000);
        Assert.Equal(65, countBounded.Count);
        Assert.Equal(true, countBounded[^1].Value);
    }

    [Fact]
    public void InstrumentationSummaryHandlesDefaultsTimingAndMalformedOmissionMetadata()
    {
        Assert.Empty(InvokeStatic<IReadOnlyList<WorkExecutionInstrumentationSummary>>(
            "CreateInstrumentationSummary",
            (WorkProfileSnapshot?)null));

        var children = new[]
        {
            Node(
                WorkProfileMetricType.Timing,
                7,
                "timing",
                context: null,
                instrumentation: "sql"),
            Node(
                WorkProfileMetricType.Scope,
                1,
                "Automatic instrumentation truncated",
                new
                {
                    OmittedByInstrumentation = new Dictionary<string, object?>
                    {
                        ["sql"] = 3,
                        ["zero"] = 0,
                        ["invalid"] = "many",
                    },
                },
                instrumentation: ""),
            Node(
                WorkProfileMetricType.Scope,
                1,
                "Automatic instrumentation truncated",
                JsonSerializer.SerializeToElement(new
                {
                    omittedByInstrumentation = new { http = 2 },
                }),
                instrumentation: "http"),
            Node(
                WorkProfileMetricType.Scope,
                1,
                "Automatic instrumentation truncated",
                JsonSerializer.SerializeToElement(new { unrelated = true }),
                instrumentation: "misc"),
            Node(
                WorkProfileMetricType.Scope,
                1,
                "ordinary",
                new ThrowingJsonValue(),
                instrumentation: "misc"),
        };
        var profile = new WorkProfileSnapshot(
            Node(WorkProfileMetricType.Scope, 10, "root", null, children, " "),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var summary = InvokeStatic<IReadOnlyList<WorkExecutionInstrumentationSummary>>(
            "CreateInstrumentationSummary",
            profile);

        var application = Assert.Single(summary, item => item.Instrumentation == WorkProfileInstrumentation.Application);
        var sql = Assert.Single(summary, item => item.Instrumentation == "sql");
        var http = Assert.Single(summary, item => item.Instrumentation == "http");
        Assert.Equal(2, application.NodeCount);
        Assert.Equal(3, sql.OmittedNodeCount);
        Assert.Equal(1, sql.TimingCount);
        Assert.Equal(7, sql.TotalTimingMilliseconds);
        Assert.Equal(7, sql.MaximumTimingMilliseconds);
        Assert.Equal(2, http.OmittedNodeCount);
    }

    [Fact]
    public void TruncationAndPropertyLookupCoverNullExactCamelAndMissingForms()
    {
        Assert.Equal("abc", InvokeStatic<string>("Truncate", "abcdef", 3));
        Assert.Equal("abc", InvokeStatic<string>("Truncate", "abc", 3));
        Assert.Null(InvokeStatic<string?>("TruncateNullable", null, 3));
        Assert.Equal("ab", InvokeStatic<string?>("TruncateNullable", "abcd", 2));

        using var document = JsonDocument.Parse("""{"PascalName":1,"camelName":2}""");
        Assert.True(InvokeTryGetProperty(document.RootElement, "PascalName", out var exact));
        Assert.Equal(1, exact.GetInt32());
        Assert.True(InvokeTryGetProperty(document.RootElement, "CamelName", out var camel));
        Assert.Equal(2, camel.GetInt32());
        Assert.False(InvokeTryGetProperty(document.RootElement, "MissingName", out _));
    }

    [Fact]
    public void StructuredPropertyValuesBoundPrimitiveStringObjectAndNullTextShapes()
    {
        AssertCapturedProperty(null, 4, null, 0, expectedTruncated: false);
        AssertCapturedProperty(42, 1, 42, 1, expectedTruncated: false);
        AssertCapturedProperty("abcdef", 3, "abc", 3, expectedTruncated: true);
        AssertCapturedProperty(new TextValue("value"), 20, "value", 5, expectedTruncated: false);
        AssertCapturedProperty(new NullTextValue(), 20, string.Empty, 0, expectedTruncated: false);
        AssertCapturedProperty(new TextValue("value"), -1, string.Empty, 0, expectedTruncated: true);
    }

    [Fact]
    public async Task ProfileBoundsRejectNodeCountPayloadSizeAndSerializationFailures()
    {
        var child = Node(WorkProfileMetricType.Scope, 1, "child", null, instrumentation: "test");
        var ordinary = new WorkProfileSnapshot(
            Node(WorkProfileMetricType.Scope, 1, "root", null, [child], "test"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var invalid = new WorkProfileSnapshot(
            Node(WorkProfileMetricType.Scope, 1, "root", new ThrowingJsonValue(), instrumentation: "test"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        await using var nodeBounded = Coordinator(new() { MaximumProfileNodeCount = 1 });
        await using var byteBounded = Coordinator(new() { MaximumProfileJsonLength = 1 });
        await using var valid = Coordinator(new());

        Assert.False(InvokeInstance<bool>(nodeBounded, "IsProfileWithinBounds", ordinary));
        Assert.False(InvokeInstance<bool>(byteBounded, "IsProfileWithinBounds", ordinary));
        Assert.False(InvokeInstance<bool>(valid, "IsProfileWithinBounds", invalid));
        Assert.True(InvokeInstance<bool>(valid, "IsProfileWithinBounds", ordinary));
    }

    [Fact]
    public async Task BoundBeginCompletionAndLogQueuesWithoutLosingWorkerOutcomes()
    {
        var logger = new RecordingLogger();
        var configuration = new WorkSystemExecutionDiagnosticsPersistenceConfiguration
        {
            IsEnabled = true,
            ChannelCapacity = 1,
            ControlOperationCapacity = 0,
            MaximumPendingLogBytes = 1_000_000,
            MaximumLogsPerIteration = 1,
        };
        await using var coordinator = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            null,
            new TestExecutionDiagnosticsRepository(),
            configuration,
            logger);
        MarkInitializedWithoutStartingWriter(coordinator);
        var first = CreateRunningWorker("diagnostics.queue.first");
        var firstIteration = Assert.IsType<WorkerIterationSnapshot>(first.ToSnapshot().CurrentIteration);

        coordinator.ObserveIteration(first, firstIteration);
        coordinator.ObserveIteration(first, firstIteration);
        coordinator.CaptureLog(
            first,
            DateTimeOffset.UtcNow,
            "diagnostics.queue",
            LogLevel.Information,
            new EventId(1),
            retainedEntry: null,
            logState: "throws",
            exception: null,
            formatter: static (_, _) => throw new InvalidOperationException("formatter failed"),
            traceId: null,
            spanId: null);
        coordinator.CaptureLog(
            first,
            DateTimeOffset.UtcNow,
            "diagnostics.queue",
            LogLevel.Information,
            new EventId(2),
            retainedEntry: null,
            logState: "captured",
            exception: null,
            formatter: static (state, _) => state,
            traceId: null,
            spanId: null);
        coordinator.CaptureLog(
            first,
            DateTimeOffset.UtcNow,
            "diagnostics.queue",
            LogLevel.Information,
            new EventId(3),
            retainedEntry: null,
            logState: "over-count-limit",
            exception: null,
            formatter: static (_, _) => throw new InvalidOperationException("count-limited formatter must not run"),
            traceId: null,
            spanId: null);

        var second = CreateRunningWorker("diagnostics.queue.second");
        coordinator.ObserveIteration(
            second,
            Assert.IsType<WorkerIterationSnapshot>(second.ToSnapshot().CurrentIteration));
        first.Complete(WorkExecutionResult.Success());
        coordinator.ObserveIteration(first, Assert.Single(first.ToSnapshot().Iterations));
        coordinator.ObserveIteration(first, Assert.Single(first.ToSnapshot().Iterations));

        Assert.Contains(logger.Messages, message => message.Contains("bounded writer queue was full", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("could not be bounded and captured", StringComparison.Ordinal));

        await using var byteBounded = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            null,
            new TestExecutionDiagnosticsRepository(),
            configuration with { MaximumPendingLogBytes = 1 },
            logger);
        MarkInitializedWithoutStartingWriter(byteBounded);
        var boundedWorker = CreateRunningWorker("diagnostics.queue.bytes");
        byteBounded.ObserveIteration(
            boundedWorker,
            Assert.IsType<WorkerIterationSnapshot>(boundedWorker.ToSnapshot().CurrentIteration));
        byteBounded.CaptureLog(
            boundedWorker,
            DateTimeOffset.UtcNow,
            "diagnostics.queue",
            LogLevel.Information,
            new EventId(3),
            retainedEntry: null,
            logState: new string('x', 100),
            exception: null,
            formatter: static (state, _) => state,
            traceId: null,
            spanId: null);

        await using var iterationByteBounded = new WorkExecutionDiagnosticsCoordinator(
            WorkSystemId.New(),
            null,
            new TestExecutionDiagnosticsRepository(),
            configuration with { MaximumLogBytesPerIteration = 1 },
            logger);
        MarkInitializedWithoutStartingWriter(iterationByteBounded);
        var iterationBoundedWorker = CreateRunningWorker("diagnostics.queue.iteration-bytes");
        iterationByteBounded.ObserveIteration(
            iterationBoundedWorker,
            Assert.IsType<WorkerIterationSnapshot>(iterationBoundedWorker.ToSnapshot().CurrentIteration));
        iterationByteBounded.CaptureLog(
            iterationBoundedWorker,
            DateTimeOffset.UtcNow,
            "diagnostics.queue",
            LogLevel.Information,
            new EventId(4),
            retainedEntry: null,
            logState: "over-iteration-byte-limit",
            exception: null,
            formatter: static (state, _) => state,
            traceId: null,
            spanId: null);
    }

    private static WorkExecutionDiagnosticsCoordinator Coordinator(
        WorkSystemExecutionDiagnosticsPersistenceConfiguration configuration)
        => new(
            WorkSystemId.New(),
            null,
            repository: null,
            configuration,
            logger: null);

    private static void MarkInitializedWithoutStartingWriter(WorkExecutionDiagnosticsCoordinator coordinator)
        => typeof(WorkExecutionDiagnosticsCoordinator).GetField(
            "initialized",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, 1);

    private static WorkerRecord CreateRunningWorker(string name)
    {
        var definition = WorkDefinition.Create(name, configuration: WorkConfiguration.Default with
        {
            ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
            },
        });
        var now = DateTimeOffset.UtcNow;
        var worker = new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.Empty,
            WorkerOptions.Default,
            definition.Configuration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: now,
            updatedAt: now);
        var outcome = worker.Start(
            worker.Revision,
            advancesRevision: false,
            out _,
            CancellationToken.None);
        Assert.True(outcome.IsAccepted);
        return worker;
    }

    private static void AssertCapturedProperty(
        object? value,
        int maximumLength,
        object? expected,
        int expectedLength,
        bool expectedTruncated)
    {
        var method = typeof(WorkExecutionDiagnosticsCoordinator).GetMethod(
            "CapturePropertyValue",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] arguments = [value, maximumLength, null, null];

        var captured = method.Invoke(null, arguments);

        Assert.Equal(expected, captured);
        Assert.Equal(expectedLength, arguments[2]);
        Assert.Equal(expectedTruncated, arguments[3]);
    }

    private static T InvokeInstance<T>(object target, string methodName, params object?[] arguments)
        => (T)target.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments)!;

    private static WorkProfileSnapshotNode Node(
        WorkProfileMetricType metricType,
        long milliseconds,
        string label,
        object? context,
        string instrumentation)
        => Node(metricType, milliseconds, label, context, [], instrumentation);

    private static WorkProfileSnapshotNode Node(
        WorkProfileMetricType metricType,
        long milliseconds,
        string label,
        object? context,
        IReadOnlyList<WorkProfileSnapshotNode> children,
        string instrumentation)
        => new(metricType, milliseconds, milliseconds, label, context, children, instrumentation);

    private static T InvokeStatic<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(WorkExecutionDiagnosticsCoordinator)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == methodName);
        if (method.IsGenericMethodDefinition)
        {
            method = method.MakeGenericMethod(arguments[0]!.GetType());
        }

        return (T)method.Invoke(null, arguments)!;
    }

    private static bool InvokeTryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        object?[] arguments = [element, name, null];
        var result = InvokeStatic<bool>("TryGetProperty", arguments);
        value = (JsonElement)arguments[2]!;
        return result;
    }

    private sealed class ThrowingJsonValue
    {
        public string Value => throw new InvalidOperationException("Cannot serialize diagnostic context.");
    }

    private sealed record TextValue(string Text)
    {
        public override string ToString() => this.Text;
    }

    private sealed class NullTextValue
    {
        public override string? ToString() => null;
    }

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class RecordingLogger : ILogger<WorkExecutionDiagnosticsCoordinator>
    {
        public List<Entry> Entries { get; } = [];

        public IEnumerable<string> Messages => this.Entries.Select(entry => entry.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Entries.Add(new(logLevel, eventId, exception, formatter(state, exception)));

        public sealed record Entry(
            LogLevel Level,
            EventId EventId,
            Exception? Exception,
            string Message);
    }
}
