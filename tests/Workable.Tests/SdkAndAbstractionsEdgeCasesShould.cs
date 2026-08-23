using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Sdk")]
public sealed class SdkAndAbstractionsEdgeCasesShould
{
    [Fact]
    public void CompareChangeKeysWithNullIdentityValueAndDefinitionScope()
    {
        var key = WorkChangeKey.Definition(" orders.run ");
        var same = WorkChangeKey.Definition("orders.run");
        var other = WorkChangeKey.Definition("orders.cancel");
        var comparer = WorkChangeKey.ScopeAwareComparer;

        Assert.False(key.Equals(null));
        Assert.True(key.Equals(same));
        Assert.False(key.Equals(other));
        Assert.True(comparer.Equals(key, key));
        Assert.False(comparer.Equals(key, null));
        Assert.False(comparer.Equals(null, key));
        Assert.True(comparer.Equals(
            key.ScopeToDefinition(" ORDERS.RUN "),
            same.ScopeToDefinition("orders.run")));
        Assert.False(comparer.Equals(
            key.ScopeToDefinition("orders.run"),
            same.ScopeToDefinition("orders.other")));
    }

    [Fact]
    public void ResolveFailureMetadataAndLogFallbackShapes()
    {
        using var document = JsonDocument.Parse("""{"string":" System.InvalidOperationException ","number":42}""");
        var declared = new WorkMessage(
            "failed",
            WorkMessageSeverity.Error,
            " declared ",
            Metadata: new Dictionary<string, object?>
            {
                ["failureSource"] = "executionContext",
                ["exceptionType"] = document.RootElement.GetProperty("string"),
                ["exceptionMessage"] = document.RootElement.GetProperty("number"),
            });

        var fromMetadata = WorkerIterationFailureResolver.Resolve([declared], null, "fallback");
        Assert.Equal(WorkerIterationFailureKind.Exception, fromMetadata.Kind);
        Assert.Equal("42", fromMetadata.Message);
        Assert.Equal("System.InvalidOperationException", fromMetadata.ExceptionType);
        Assert.True(fromMetadata.DeclaredByWork);

        var now = DateTimeOffset.UtcNow;
        var warning = Log(now.AddMinutes(1), LogLevel.Warning, null, null);
        var errorWithoutType = Log(now, LogLevel.Error, null, " logged failure ");
        var fromLog = WorkerIterationFailureResolver.Resolve(null, [errorWithoutType, warning], "fallback");
        Assert.Equal(WorkerIterationFailureKind.Failure, fromLog.Kind);
        Assert.Equal("logged failure", fromLog.Message);

        var fallback = WorkerIterationFailureResolver.Resolve(
            [new WorkMessage("failed", WorkMessageSeverity.Error, "   ")],
            [],
            "fallback");
        Assert.Equal("fallback", fallback.Message);
        Assert.Equal(
            "fallback",
            WorkerIterationFailureResolver.Resolve(messages: null, logs: null, "fallback").Message);
    }

    [Fact]
    public async Task ValidateOrderAndInvokeRawAndTypedInitializersAcrossInputShapes()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WorkInitializationRegistration.Create<object>(WorkInitializationTiming.OncePerWorker, null));
        Assert.Throws<InvalidOperationException>(() =>
            WorkInitializationRegistration.Create<MultiTypedInitializer>(WorkInitializationTiming.OnceLazy, null));

        var raw = WorkInitializationRegistration.Create<RawInitializer>(WorkInitializationTiming.OncePerWorker, 2);
        var typed = WorkInitializationRegistration.Create<MultiTypedInitializer>(WorkInitializationTiming.OncePerWorker, null);
        Assert.False(raw.IsTyped);
        Assert.True(typed.IsTyped);
        Assert.Same(raw, WorkInitializationRegistration.Order([raw])[0]);
        Assert.Equal([raw, typed], WorkInitializationRegistration.Order([typed, raw]));
        Assert.False((await raw.Invoke(new RawInitializer(), null!, null, CancellationToken.None)).HasErrors);

        var initializer = new MultiTypedInitializer();
        Assert.False((await typed.Invoke(initializer, null!, WorkInput.FromValue(42), CancellationToken.None)).HasErrors);
        Assert.Equal("int:42", initializer.LastInvocation);

        var unmatchedClrType = WorkInput.FromJson("\"value\"", typeof(decimal));
        Assert.False((await typed.Invoke(initializer, null!, unmatchedClrType, CancellationToken.None)).HasErrors);
        Assert.Equal("string:value", initializer.LastInvocation);

        var missing = await typed.Invoke(initializer, null!, WorkInput.Empty, CancellationToken.None);
        Assert.Contains(missing.Messages, message => message.Code == "workable.initialization.input_required");
        var malformed = await typed.Invoke(
            initializer,
            null!,
            WorkInput.FromJson("{", typeof(string)),
            CancellationToken.None);
        Assert.Contains(malformed.Messages, message => message.Code == "workable.initialization.input_invalid_json");
    }

    [Fact]
    public void RequireEveryExecutionAttemptPayloadAndRejectMissingPayloads()
    {
        var result = WorkExecutionResult.Success();
        Assert.Same(result, WorkerExecutionAttempt.Completed(result).RequiredResult);
        Assert.Throws<InvalidOperationException>(() =>
            _ = WorkerExecutionAttempt.ExceptionFailed(
                WorkMessage.Error("failed", "failed"),
                new InvalidOperationException(),
                WorkExceptionClassification.NonTransient).RequiredResult);

        var exception = new InvalidOperationException("failed");
        var message = WorkMessage.Error("failed", "failed");
        var failed = WorkerExecutionAttempt.ExceptionFailed(
            message,
            exception,
            WorkExceptionClassification.Transient);
        Assert.Same(message, failed.RequiredExceptionFailureMessage);
        Assert.Same(exception, failed.RequiredException);
        Assert.Equal(WorkExceptionClassification.Transient, failed.RequiredExceptionClassification);
        Assert.Throws<InvalidOperationException>(() =>
            _ = WorkerExecutionAttempt.Completed(result).RequiredExceptionFailureMessage);
        Assert.Throws<InvalidOperationException>(() =>
            _ = WorkerExecutionAttempt.Completed(result).RequiredException);
        Assert.Throws<InvalidOperationException>(() =>
            _ = WorkerExecutionAttempt.Completed(result).RequiredExceptionClassification);
    }

    [Fact]
    public void AdaptTypedSchemasContextsAutomaticProfilingAndTaskResultsDefensively()
    {
        var custom = WorkSchema.FromType<Guid>();
        var definition = WorkDefinition.Create("typed") with
        {
            InputSchema = custom,
            OutputSchema = custom,
        };
        var preservedInput = WorkExecutorAdapterFactory.ApplyTypedSchemas<string>(definition);
        var preservedBoth = WorkExecutorAdapterFactory.ApplyTypedSchemas<string, int>(definition);
        Assert.Same(custom, preservedInput.InputSchema);
        Assert.Same(custom, preservedBoth.InputSchema);
        Assert.Same(custom, preservedBoth.OutputSchema);

        var origin = WorkOrigin.Create(WorkInvocationChannel.InProcess, WorkActor.Unknown);
        Assert.Throws<ArgumentNullException>(() => new WorkRequestContext(null!));
        var context = new WorkRequestContext(origin);
        Assert.Same(context, context.WithSurface(context.Surface));
        Assert.NotSame(context, context.WithSurface(WorkOriginSurface.WorkableAdapter));

        IWorkAutomaticProfiler profiler = new AutomaticProfiler(admitTiming: true);
        Assert.True(profiler.TryStartAutomaticTiming("test", "timing", () => new object(), out var captured, out var scope));
        Assert.NotNull(captured);
        Assert.NotNull(scope);
        IWorkAutomaticProfiler rejecting = new AutomaticProfiler(admitTiming: false);
        Assert.False(rejecting.TryStartAutomaticTiming("test", "timing", () => new object(), out captured, out scope));
        Assert.Null(captured);
        Assert.Null(scope);

        var getTaskResult = typeof(TypedWorkExecutorAdapter).GetMethod(
            "GetTaskResult",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(42, getTaskResult.Invoke(null, [Task.FromResult(42)]));
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() =>
            getTaskResult.Invoke(null, [Task.FromResult<object?>(null)])).InnerException);
        var nonGenericTask = new Task(static () => { });
        nonGenericTask.RunSynchronously();
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() =>
            getTaskResult.Invoke(null, [nonGenericTask])).InnerException);
    }

    [Fact]
    public void DeserializeStatusPayloadsAndEvaluateEveryCoordinationParentSwitch()
    {
        var reference = new WorkerIterationReference(WorkerId.New(), 1);
        WorkIterationStatusItem Item(JsonElement? data) => new(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            null,
            reference,
            1,
            "status.work",
            "progress",
            data);

        Assert.Null(Item(data: null).DeserializeData<string>());
        Assert.Null(Item(JsonSerializer.SerializeToElement<object?>(null)).DeserializeData<string>());
        Assert.Equal(42, Item(JsonSerializer.SerializeToElement(42)).DeserializeData<int>());
        Assert.Equal(
            "value",
            Item(JsonSerializer.SerializeToElement("value")).DeserializeData<string>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false }));

        var disabled = WorkCoordinationConfiguration.Default;
        var local = disabled with { IsEnabled = true };
        var persistent = local with { Storage = WorkCoordinationStorage.Persistent };
        Assert.False(disabled.RequiresPersistenceStore);
        Assert.False(local.RequiresPersistenceStore);
        Assert.True(persistent.RequiresPersistenceStore);

        var persistentConcurrency = persistent with
        {
            Concurrency = WorkConcurrencyConfiguration.Default with { IsEnabled = true },
        };
        Assert.False(disabled.IsPersistentConcurrencyEnabled);
        Assert.False(local.IsPersistentConcurrencyEnabled);
        Assert.True(persistentConcurrency.IsPersistentConcurrencyEnabled);
    }

    [Fact]
    public void TypedResultsStartupSourcesAndDurabilitySamplesCoverBothOutcomeBranches()
    {
        var successful = WorkExecutionResult<int>.Success(42);
        var failed = WorkExecutionResult<int>.Failure(
            [WorkMessage.Error("typed.failed", "Typed execution failed.")],
            7);
        Assert.False(successful.HasErrors);
        Assert.True(failed.HasErrors);

        var services = new ServiceCollection();
        Assert.Same(services, services.AddWorkableStartupWorkSource<TestStartupSource>());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TestStartupSource>());

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, new WorkQueueDurabilityClaimSample(
            1,
            now,
            now,
            10,
            TimeSpan.Zero,
            TimeSpan.Zero).ClaimedEntriesPerSecond);
        Assert.Equal(20, new WorkQueueDurabilityClaimSample(
            2,
            now,
            now,
            10,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.Zero).ClaimedEntriesPerSecond);
    }

    [Fact]
    public void MatchWorkEventsAcrossEveryDefinitionAndRelationshipBoundary()
    {
        var workerId = WorkerId.New();
        var subject = new WorkSubjectId("invoice", "42");
        var concurrency = new WorkConcurrencyKey("tenant", "west");
        var identifier = new WorkIdentifier("order", "100");
        var definitionScope = new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, "billing.close");
        var workEvent = new WorkEvent(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            null,
            workerId,
            WorkDefinitionId.New(),
            "billing.close",
            subject,
            concurrency,
            new HashSet<WorkIdentifier> { identifier },
            "worker.completed",
            data: null);

        var matching = new WorkEventFilter(
            workerId,
            "BILLING.CLOSE",
            new HashSet<string> { "Billing.Close" },
            subject,
            concurrency,
            identifier,
            new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Identifier, identifier.Type, identifier.Value) },
            "WORKER.COMPLETED",
            new HashSet<string> { "Worker.Completed" })
        {
            DefinitionKind = WorkEventDefinitionKind.Work,
            AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope> { definitionScope },
        };
        Assert.True(matching.Matches(workEvent));

        Assert.False((matching with { DefinitionKind = WorkEventDefinitionKind.Workflow }).Matches(workEvent));
        Assert.False((matching with
        {
            AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope>
            {
                new(WorkEventDefinitionKind.Work, "billing.other"),
            },
        }).Matches(workEvent));
        Assert.False((matching with { WorkerId = WorkerId.New() }).Matches(workEvent));
        Assert.False((matching with { DefinitionName = "billing.other" }).Matches(workEvent));
        Assert.False((matching with { DefinitionNames = new HashSet<string> { "billing.other" } }).Matches(workEvent));
        Assert.False((matching with { SubjectId = new WorkSubjectId("invoice", "other") }).Matches(workEvent));
        Assert.False((matching with { ConcurrencyKey = new WorkConcurrencyKey("tenant", "east") }).Matches(workEvent));
        Assert.False((matching with { Identifier = new WorkIdentifier("order", "other") }).Matches(workEvent));
        Assert.False((matching with
        {
            Keys = new HashSet<WorkEventKeyFilter> { new(WorkKeyKind.Subject, "invoice", "other") },
        }).Matches(workEvent));
        Assert.False((matching with { EventType = "worker.failed" }).Matches(workEvent));
        Assert.False((matching with { EventTypes = new HashSet<string> { "worker.failed" } }).Matches(workEvent));

        var unnamed = new WorkEvent(
            workEvent.OccurredAt,
            workEvent.WorkSystemId,
            null,
            workerId,
            null,
            workDefinitionName: null,
            subject,
            concurrency,
            workEvent.Identifiers,
            workEvent.EventType,
            data: null);
        Assert.False(new WorkEventFilter(
            DefinitionNames: new HashSet<string> { "billing.close" }).Matches(unnamed));
        Assert.True(new WorkEventFilter(
            DefinitionNames: new HashSet<string>(),
            Keys: new HashSet<WorkEventKeyFilter>(),
            EventTypes: new HashSet<string>())
        {
            AuthorizedDefinitions = new HashSet<WorkEventDefinitionScope>(),
        }.Matches(workEvent));
    }

    [Fact]
    public void ValidatePublicSdkAndEventBoundaryValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkRecurrenceConfiguration.Every(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkRecurrenceConfiguration.Every(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => new WorkflowStepReference<object>(" "));

        var origin = WorkOrigin.Create(WorkInvocationChannel.InProcess);
        Assert.Same(origin, origin.WithSurface(WorkOriginSurface.HostApplication));
        Assert.NotSame(origin, origin.WithSurface(WorkOriginSurface.WorkableAdapter));

        Assert.Null(WorkInput.Empty.ToValue(typeof(int)));
        Assert.Equal(42, WorkInput.FromValue(42).ToValue(typeof(int)));
        Assert.Null(WorkInput.FromJson("42", identifiers: []).Identifiers);

        var rawInput = WorkInput.FromValue(7);
        Assert.Same(rawInput, StartupWorkRequest.ForName<WorkInput>("startup", rawInput).Input);
        Assert.Same(rawInput, StartupWorkRequest.ForDefinition<WorkInput>(WorkDefinitionId.New(), rawInput).Input);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkChange(0, DateTimeOffset.UtcNow, WorkChangeKey.System()));
        var systemId = WorkSystemId.New();
        Assert.Contains(systemId.ToString(), new WorkSystemAuthorizationRequiredException(systemId, null).Message, StringComparison.Ordinal);
        Assert.Contains("named", new WorkSystemAuthorizationRequiredException(systemId, "named").Message, StringComparison.Ordinal);

        var scopeComparer = WorkEventDefinitionScopeComparer.Instance;
        var workScope = new WorkEventDefinitionScope(WorkEventDefinitionKind.Work, "orders");
        Assert.True(scopeComparer.Equals(workScope, new(WorkEventDefinitionKind.Work, "ORDERS")));
        Assert.False(scopeComparer.Equals(workScope, new(WorkEventDefinitionKind.Workflow, "orders")));
        Assert.False(scopeComparer.Equals(workScope, new(WorkEventDefinitionKind.Work, "other")));

        var noData = CreateEvent(data: null);
        var nullData = CreateEvent(JsonSerializer.SerializeToElement<object?>(null));
        var numberData = CreateEvent(JsonSerializer.SerializeToElement(42));
        Assert.Null(noData.DeserializeData<int?>());
        Assert.Null(nullData.DeserializeData<int?>());
        Assert.Equal(42, numberData.DeserializeData<int>());
    }

    [Fact]
    public void FormatDurabilityFailuresAndNonJsonFailureMetadata()
    {
        var first = new WorkQueueDurabilityLease(
            WorkerId.New(),
            "owner",
            "lease-1");
        var second = new WorkQueueDurabilityLease(
            WorkerId.New(),
            "owner",
            "lease-2");

        Assert.Equal(
            "Durable queue lease ownership was lost.",
            new WorkQueueDurabilityLeaseLostException([]).Message);
        Assert.Contains(
            first.WorkerId.ToString(),
            new WorkQueueDurabilityLeaseLostException([first]).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "2 workers",
            new WorkQueueDurabilityLeaseLostException([first, second]).Message,
            StringComparison.Ordinal);

        var metadata = new WorkMessage(
            "failed",
            WorkMessageSeverity.Error,
            "failed",
            Metadata: new Dictionary<string, object?>
            {
                ["exceptionType"] = typeof(InvalidOperationException),
                ["exceptionMessage"] = 42,
            });
        var failure = WorkerIterationFailureResolver.Resolve([metadata], logs: null, "fallback");
        Assert.Contains(nameof(InvalidOperationException), failure.ExceptionType, StringComparison.Ordinal);
        Assert.Equal("42", failure.Message);

        var invalid = WorkActionOutcome.Invalid(
            WorkAction.Cancel,
            worker: null,
            [WorkMessage.Error("invalid", "invalid")]);
        Assert.Null(invalid.WorkerId);
        Assert.Null(invalid.Worker);
    }

    private static WorkerLogEntry Log(
        DateTimeOffset occurredAt,
        LogLevel level,
        string? exceptionType,
        string? exceptionMessage)
        => new(
            occurredAt,
            WorkerId.New(),
            WorkDefinitionId.New(),
            "SdkAndAbstractionsEdgeCases",
            level,
            new EventId(1),
            "log")
        {
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage,
        };

    private static WorkEvent CreateEvent(JsonElement? data)
        => new(
            DateTimeOffset.UtcNow,
            WorkSystemId.New(),
            workSystemName: null,
            workerId: null,
            workDefinitionId: null,
            workDefinitionName: null,
            subjectId: null,
            concurrencyKey: null,
            identifiers: new HashSet<WorkIdentifier>(),
            eventType: "sdk.boundary",
            data);

    private sealed class RawInitializer : IWorkInitializer
    {
        public Task<WorkExecutionResult> Initialize(
            IWorkExecutionContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class MultiTypedInitializer : IWorkInitializer<string>, IWorkInitializer<int>
    {
        public string? LastInvocation { get; private set; }

        Task<WorkExecutionResult> IWorkInitializer<string>.Initialize(
            IWorkExecutionContext context,
            string input,
            CancellationToken cancellationToken)
        {
            this.LastInvocation = $"string:{input}";
            return Task.FromResult(WorkExecutionResult.Success());
        }

        Task<WorkExecutionResult> IWorkInitializer<int>.Initialize(
            IWorkExecutionContext context,
            int input,
            CancellationToken cancellationToken)
        {
            this.LastInvocation = $"int:{input}";
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class AutomaticProfiler(bool admitTiming) : IWorkAutomaticProfiler
    {
        public bool TryAddAutomaticInfo(string instrumentation, string name, object? context = null)
            => true;

        public bool TryStartAutomaticTiming(
            string instrumentation,
            string name,
            object? context,
            out IWorkProfileScope? scope)
        {
            scope = admitTiming ? new ProfileScope() : null;
            return admitTiming;
        }
    }

    private sealed class ProfileScope : IWorkProfileScope
    {
        public void SetResult(object? context = null)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestStartupSource : IStartupWorkSource
    {
        public Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartupWorkRequest>>([]);
    }
}
