using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Views")]
public sealed class WorkableViewQueryAdapterBranchShould
{
    [Theory]
    [InlineData("system", "system")]
    [InlineData("catalog", "definition")]
    [InlineData("workers", "worker")]
    [InlineData("failedWorkers", "worker")]
    [InlineData("iterations", "worker")]
    [InlineData("failedIterations", "worker")]
    [InlineData("completedIterations", "worker")]
    [InlineData("workflowRun", "definition")]
    [InlineData("workflowRuns", "definition")]
    [InlineData("systemDiagnostics", "diagnostics")]
    [InlineData("queueDiagnostics", "diagnostics")]
    [InlineData("readModelDiagnostics", "diagnostics")]
    [InlineData("retentionDiagnostics", "diagnostics")]
    [InlineData("concurrencyDiagnostics", "diagnostics")]
    [InlineData("durabilityDiagnostics", "diagnostics")]
    [InlineData("idempotencyDiagnostics", "diagnostics")]
    [InlineData("futureComponent", "system")]
    public void ChangeRoutingCoversEveryComponentFamily(string componentType, string changeType)
    {
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest("component", componentType),
        ]);
        var change = changeType switch
        {
            "system" => WorkChangeKey.System(),
            "definition" => WorkChangeKey.Definition("branch.work"),
            "worker" => WorkChangeKey.Worker(WorkerId.New()),
            "diagnostics" => WorkChangeKey.Diagnostics("branch"),
            _ => throw new InvalidOperationException(),
        };

        Assert.True(new WorkableViewQueryAdapter().ShouldPublishForChanges(
            "overview",
            criteria,
            [change]));
    }

    [Fact]
    public void ChangeRoutingExercisesWorkflowDiagnosticsAndGridShortCircuits()
    {
        var adapter = new WorkableViewQueryAdapter();
        static WorkViewCriteria One(string type, object? options = null)
            => new(Components:
            [
                new WorkComponentRequest(
                    "component",
                    type,
                    options is null ? null : JsonSerializer.SerializeToElement(options)),
            ]);

        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            One("workflowRun"),
            [WorkChangeKey.Diagnostics("none")]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            One("workflowRun"),
            [WorkChangeKey.System()]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            One("queueDiagnostics"),
            [new WorkChangeKey((WorkChangeKind)99, "unknown", "none")]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            One("queueDiagnostics"),
            [WorkChangeKey.System()]));

        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            One("workerGrid", new { keyKind = WorkKeyKind.Subject }),
            [WorkChangeKey.Subject(new WorkSubjectId("invoice", "1"))]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            One("workerGrid", new { keyType = "invoice" }),
            [WorkChangeKey.Subject(new WorkSubjectId("invoice", "2"))]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            One("iterationGrid", new { keyValue = "3" }),
            [WorkChangeKey.Identifier(new WorkIdentifier("invoice", "3"))]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            One("workerGrid", new { actorId = "owner", keyType = "invoice" }),
            [WorkChangeKey.Subject(new WorkSubjectId("invoice", "2"))]));
    }

    [Fact]
    public void PublicNormalizationCoversNullUnknownAndActorOptionShapes()
    {
        var adapter = new WorkableViewQueryAdapter();

        Assert.NotEmpty(adapter.NormalizeComponentCriteria().Components!);
        Assert.False(adapter.RequiresIntervalPublish("unknown-view"));
        Assert.Throws<ArgumentException>(() => adapter.NormalizeComponentCriteria(
            new WorkComponentCriteria(Components: [null!]))) ;

        var scoped = adapter.NormalizeActorWorkerViewCriteria(
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("worker", "workerGrid"),
                new WorkComponentRequest(
                    "worker-with-options",
                    "workerGrid",
                    JsonSerializer.SerializeToElement(new { ACTORID = "replaced", take = 5 })),
            ]),
            " owner ");
        Assert.All(scoped.Components!, component =>
            Assert.Equal("owner", component.Options?.GetProperty("actorId").GetString()));
        Assert.Throws<ArgumentException>(() => adapter.NormalizeActorWorkerViewCriteria(
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "worker",
                    "workerGrid",
                    JsonSerializer.SerializeToElement(42)),
            ]),
            "owner"));
    }

    [Fact]
    public void PrivateBoundaryHelpersPreserveScopeShapeAndFallbackSemantics()
    {
        Assert.Equal(WorkComponentShapes.Detailed, InvokeStatic<string>("NormalizeComponentShape", (object?)null));
        Assert.Equal(WorkComponentShapes.Detailed, InvokeStatic<string>("NormalizeComponentShape", " "));
        Assert.Equal(WorkComponentShapes.Compact, InvokeStatic<string>("NormalizeComponentShape", " COMPACT "));
        Assert.Equal(WorkComponentShapes.Standard, InvokeStatic<string>("NormalizeComponentShape", "standard"));
        Assert.Equal(WorkComponentShapes.Detailed, InvokeStatic<string>("NormalizeComponentShape", "detailed"));
        Assert.Equal("future", InvokeStatic<string>("NormalizeComponentShape", " Future "));

        Assert.True(InvokeStatic<bool>("MatchesScope", "work", "Billing:Child", null));
        Assert.True(InvokeStatic<bool>(
            "MatchesScope",
            "work",
            "Billing:Child",
            new WorkSystemCriteria(DefinitionName: "WORK")));
        Assert.False(InvokeStatic<bool>(
            "MatchesScope",
            "other",
            "Billing:Child",
            new WorkSystemCriteria(DefinitionName: "work")));
        Assert.True(InvokeStatic<bool>(
            "MatchesScope",
            "work",
            "Billing:Child",
            new WorkSystemCriteria(Category: " ")));
        Assert.True(InvokeStatic<bool>(
            "MatchesScope",
            "work",
            "Billing:Child",
            new WorkSystemCriteria(Category: "billing", IncludeSubcategories: true)));
        Assert.False(InvokeStatic<bool>(
            "MatchesScope",
            "work",
            "Billing:Child",
            new WorkSystemCriteria(Category: "shipping", IncludeSubcategories: true)));
        Assert.False(InvokeStatic<bool>(
            "MatchesScope",
            "work",
            "Billing:Child",
            new WorkSystemCriteria(Category: "billing", IncludeSubcategories: false)));

        Assert.True(InvokeStatic<bool>("ShouldFallbackToPublishing", new JsonException()));
        Assert.True(InvokeStatic<bool>("ShouldFallbackToPublishing", new InvalidOperationException()));
        Assert.True(InvokeStatic<bool>("ShouldFallbackToPublishing", new ArgumentException()));
        Assert.True(InvokeStatic<bool>("ShouldFallbackToPublishing", new NullReferenceException()));
        Assert.False(InvokeStatic<bool>("ShouldFallbackToPublishing", new Exception()));
    }

    [Fact]
    public void ChangeKeySetMatchesBlankExactAndPartialSelectors()
    {
        var workerId = WorkerId.New();
        var keys = new WorkChangeKey[]
        {
            WorkChangeKey.Worker(workerId),
            WorkChangeKey.Definition("branch.work"),
            WorkChangeKey.Actor("owner"),
            WorkChangeKey.Subject(new WorkSubjectId("invoice", "1")),
            WorkChangeKey.Concurrency(new WorkConcurrencyKey("tenant", "2")),
            WorkChangeKey.Identifier(new WorkIdentifier("order", "3")),
        };
        var setType = typeof(WorkableViewQueryAdapter)
            .GetNestedType("WorkChangeKeySet", BindingFlags.NonPublic)!;
        var set = Activator.CreateInstance(
            setType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [keys],
            culture: null)!;

        Assert.True(InvokeInstance<bool>(set, "ContainsWorker", workerId));
        Assert.False(InvokeInstance<bool>(set, "ContainsWorker", WorkerId.New()));
        Assert.False(InvokeInstance<bool>(set, "ContainsDefinition", " "));
        Assert.True(InvokeInstance<bool>(set, "ContainsDefinition", " BRANCH.WORK "));
        Assert.False(InvokeInstance<bool>(set, "ContainsActor", " "));
        Assert.True(InvokeInstance<bool>(set, "ContainsActor", " owner "));
        Assert.True(InvokeInstance<bool>(set, "ContainsStructuredKey", null, null, null));
        Assert.True(InvokeInstance<bool>(set, "ContainsStructuredKey", WorkKeyKind.Subject, "invoice", "1"));
        Assert.False(InvokeInstance<bool>(set, "ContainsStructuredKey", WorkKeyKind.Subject, "invoice", "missing"));
        Assert.True(InvokeInstance<bool>(set, "ContainsStructuredKey", (WorkKeyKind)99, null, null));
    }

    [Fact]
    public void CategoryScopeAndStructuredKeyHelpersCoverEveryDecision()
    {
        Assert.True(InvokeStatic<bool>("StartsWithCategoryPath", Array.Empty<string>(), Array.Empty<string>()));
        Assert.True(InvokeStatic<bool>("StartsWithCategoryPath", new[] { "billing" }, new[] { "BILLING" }));
        Assert.False(InvokeStatic<bool>("StartsWithCategoryPath", new[] { "billing" }, new[] { "billing", "child" }));
        Assert.False(InvokeStatic<bool>("StartsWithCategoryPath", new[] { "billing" }, new[] { "shipping" }));

        Assert.False(InvokeStatic<bool>("HasDefinitionScope", (object?)null));
        Assert.True(InvokeStatic<bool>("HasDefinitionScope", new WorkSystemCriteria(DefinitionName: "work")));
        Assert.True(InvokeStatic<bool>("HasDefinitionScope", new WorkSystemCriteria(DefinitionNames: new HashSet<string> { "work" })));
        Assert.False(InvokeStatic<bool>("HasDefinitionScope", new WorkSystemCriteria(DefinitionNames: new HashSet<string>())));
        var changesType = typeof(WorkableViewQueryAdapter).GetNestedType("WorkChangeKeySet", BindingFlags.NonPublic)!;
        var changes = Activator.CreateInstance(
            changesType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [new[] { WorkChangeKey.Definition("alpha") }],
            culture: null)!;
        Assert.False(InvokeStatic<bool>("ScopedDefinitionsMayChange", null, changes));
        Assert.True(InvokeStatic<bool>("ScopedDefinitionsMayChange", new WorkSystemCriteria(DefinitionName: "alpha"), changes));
        Assert.False(InvokeStatic<bool>("ScopedDefinitionsMayChange", new WorkSystemCriteria(DefinitionName: "beta"), changes));
        Assert.True(InvokeStatic<bool>("ScopedDefinitionsMayChange", new WorkSystemCriteria(DefinitionNames: new HashSet<string> { "beta", "alpha" }), changes));
        Assert.False(InvokeStatic<bool>("ScopedDefinitionsMayChange", new WorkSystemCriteria(DefinitionNames: new HashSet<string> { "beta" }), changes));

        var method = typeof(WorkableViewQueryAdapter)
            .GetMethod("ApplyExactStructuredKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(string));
        static string Subject(string _, WorkSubjectId key) => $"subject:{key.Type}:{key.Value}";
        static string Concurrency(string _, WorkConcurrencyKey key) => $"concurrency:{key.Type}:{key.Value}";
        static string Identifier(string _, WorkIdentifier key) => $"identifier:{key.Type}:{key.Value}";
        object?[] Prefix(WorkKeyKind? kind, string? type, string? value) =>
        [
            "original", kind, type, value,
            (Func<string, WorkSubjectId, string>)Subject,
            (Func<string, WorkConcurrencyKey, string>)Concurrency,
            (Func<string, WorkIdentifier, string>)Identifier,
        ];
        Assert.Equal("original", method.Invoke(null, Prefix(null, null, null)));
        Assert.Equal("subject:t:v", method.Invoke(null, Prefix(WorkKeyKind.Subject, "t", "v")));
        Assert.Equal("concurrency:t:v", method.Invoke(null, Prefix(WorkKeyKind.ConcurrencyKey, "t", "v")));
        Assert.Equal("identifier:t:v", method.Invoke(null, Prefix(WorkKeyKind.Identifier, "t", "v")));
        Assert.Equal("original", method.Invoke(null, Prefix((WorkKeyKind)99, "t", "v")));
    }

    [Fact]
    public void TimelineProjectionCoversEveryKindCategoryFailureAndInvalidValue()
    {
        foreach (var kind in Enum.GetValues<WorkerActivityEventKind>())
        foreach (var category in Enum.GetValues<WorkerActivityEventCategory>())
        {
            var projected = InvokeStatic<object>("CreateWorkerOverviewTimelineItem", new WorkerActivityEvent(
                $"{kind}:{category}",
                DateTimeOffset.UtcNow,
                kind,
                category,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null));
            Assert.NotNull(projected);
        }

        Assert.Throws<TargetInvocationException>(() => InvokeStatic<object>(
            "CreateWorkerOverviewTimelineItem",
            new WorkerActivityEvent(
                "invalid-kind",
                DateTimeOffset.UtcNow,
                (WorkerActivityEventKind)99,
                WorkerActivityEventCategory.SystemEvent,
                null, null, null, null, null, null, null, null, null, null)));
        Assert.Throws<TargetInvocationException>(() => InvokeStatic<object>(
            "CreateWorkerOverviewTimelineItem",
            new WorkerActivityEvent(
                "invalid-category",
                DateTimeOffset.UtcNow,
                WorkerActivityEventKind.Iteration,
                (WorkerActivityEventCategory)99,
                null, null, null, null, null, null, null, null, null, null)));

        Assert.Null(InvokeStatic<object?>("CreateWorkerOverviewFailure", null, null));
        Assert.NotNull(InvokeStatic<object?>(
            "CreateWorkerOverviewFailure",
            new WorkerIterationFailure(WorkerIterationFailureKind.Failure, "failure"),
            null));
        Assert.NotNull(InvokeStatic<object?>(
            "CreateWorkerOverviewFailure",
            new WorkerIterationFailure(WorkerIterationFailureKind.Exception, "exception"),
            null));
        Assert.Throws<TargetInvocationException>(() => InvokeStatic<object?>(
            "CreateWorkerOverviewFailure",
            new WorkerIterationFailure((WorkerIterationFailureKind)99, "invalid"),
            null));
    }

    [Fact]
    public void LiveAndRetryTimelineProjectionDistinguishesEveryCandidateShape()
    {
        var now = DateTimeOffset.UtcNow;
        var failure = new WorkerIterationFailure(WorkerIterationFailureKind.Failure, "failed");
        var paused = CreateWorker(WorkerState.Paused, now);
        var pausedIteration = CreateIteration(1, WorkCompletionStatus.Paused, failure: null, now);
        var completedIteration = CreateIteration(1, WorkCompletionStatus.Completed, failure: null, now);
        var pausedItem = InvokeStatic<WorkWorkerOverviewTimelineItem>(
            "CreateWorkerOverviewTimelineItem",
            Activity("paused", WorkerActivityEventKind.StateChange, sequence: null, state: WorkerState.Paused, failure: null, now));
        var createLiveItem = typeof(WorkableViewQueryAdapter)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateWorkerOverviewLiveStateItem" &&
                method.GetParameters()[1].ParameterType == typeof(WorkerIterationSnapshot));

        Assert.Null(createLiveItem.Invoke(null, [paused, pausedIteration, Array.Empty<WorkWorkerOverviewTimelineItem>()]));
        Assert.Null(createLiveItem.Invoke(null, [paused, completedIteration, new[] { pausedItem }]));
        Assert.NotNull(createLiveItem.Invoke(null, [paused, completedIteration, Array.Empty<WorkWorkerOverviewTimelineItem>()]));

        var latest = CreateIteration(1, WorkCompletionStatus.Failed, failure, now);
        var retrying = CreateWorker(WorkerState.Retrying, now);
        var candidates = new[]
        {
            Activity("action", WorkerActivityEventKind.ActionRequest, sequence: null, state: null, failure: null, now),
            Activity("other-sequence", WorkerActivityEventKind.Iteration, sequence: 2, state: null, failure, now),
            Activity("no-failure", WorkerActivityEventKind.Iteration, sequence: 1, state: null, failure: null, now),
            Activity("matching", WorkerActivityEventKind.Iteration, sequence: 1, state: null, failure, now),
        }.Select(item => InvokeStatic<WorkWorkerOverviewTimelineItem>(
            "CreateWorkerOverviewTimelineItem",
            item)).ToArray();

        var projected = InvokeStatic<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(
            "ApplyWorkerOverviewRetryPending",
            retrying,
            latest,
            candidates);

        Assert.Null(projected[0].Failure);
        Assert.Null(projected[1].Failure?.PendingState);
        Assert.Null(projected[2].Failure);
        Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, projected[3].Failure?.PendingState?.Mode);
    }

    [Fact]
    public void JsonOptionHelpersCoverNullUndefinedMalformedAndTypedValues()
    {
        var deserialize = typeof(WorkableViewQueryAdapter)
            .GetMethod("DeserializeOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        Assert.Equal(0, deserialize.Invoke(null, [null]));
        Assert.Equal(0, deserialize.Invoke(null, [(JsonElement?)default(JsonElement)]));
        using var number = JsonDocument.Parse("42");
        Assert.Equal(42, deserialize.Invoke(null, [(JsonElement?)number.RootElement]));
        using var malformedForInt = JsonDocument.Parse("\"not-an-int\"");
        var malformed = Assert.Throws<TargetInvocationException>(() =>
            deserialize.Invoke(null, [(JsonElement?)malformedForInt.RootElement]));
        Assert.Equal("WorkComponentValidationException", malformed.InnerException?.GetType().Name);

        Assert.Null(InvokeStatic<object?>("CreateThroughputCriteria", (object?)null));
        Assert.Null(InvokeStatic<object?>("CreateThroughputCriteria", (JsonElement?)default(JsonElement)));
        using var throughput = JsonDocument.Parse("""{"windowSeconds":120,"bucketSeconds":10}""");
        Assert.NotNull(InvokeStatic<object?>("CreateThroughputCriteria", (JsonElement?)throughput.RootElement));

        Assert.Throws<TargetInvocationException>(() => InvokeStatic<object>("GetRequiredWorkerId", (object?)null));
        using var invalidWorker = JsonDocument.Parse("""{"workerId":"invalid"}""");
        Assert.Throws<TargetInvocationException>(() => InvokeStatic<object>(
            "GetRequiredWorkerId",
            (JsonElement?)invalidWorker.RootElement));
        using var validWorker = JsonDocument.Parse($$"""{"workerId":"{{Guid.NewGuid():D}}"}""");
        Assert.NotNull(InvokeStatic<object>("GetRequiredWorkerId", (JsonElement?)validWorker.RootElement));
    }

    [Fact]
    public void PagingNormalizationAndOptionReadersCoverAllBoundaryShapes()
    {
        var slice = typeof(WorkableViewQueryAdapter)
            .GetMethod("SliceWorkerOverviewPage", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(string));
        static string Cursor(string value) => value;
        var getCursor = (Func<string, string>)Cursor;
        var items = (IReadOnlyList<string>)["a", "b", "c"];

        var first = ((IReadOnlyList<string> Items, bool HasMore))slice.Invoke(
            null,
            [items, getCursor, null, 2])!;
        var after = ((IReadOnlyList<string> Items, bool HasMore))slice.Invoke(
            null,
            [items, getCursor, "b", 2])!;
        var missing = ((IReadOnlyList<string> Items, bool HasMore))slice.Invoke(
            null,
            [items, getCursor, "missing", 5])!;
        Assert.Equal(["a", "b"], first.Items);
        Assert.True(first.HasMore);
        Assert.Equal(["c"], after.Items);
        Assert.False(after.HasMore);
        Assert.Equal(items, missing.Items);

        Assert.Equal(50, Assert.IsType<WorkIterationMessageCriteria>(
            InvokeStatic<object>("NormalizeIterationMessageCriteria", (object?)null)).Take);
        Assert.Equal(1, Assert.IsType<WorkIterationMessageCriteria>(
            InvokeStatic<object>("NormalizeIterationMessageCriteria", new WorkIterationMessageCriteria(Take: 0))).Take);
        Assert.Equal(50, Assert.IsType<WorkIterationLogCriteria>(
            InvokeStatic<object>("NormalizeIterationLogCriteria", (object?)null)).Take);
        Assert.Equal(200, Assert.IsType<WorkIterationLogCriteria>(
            InvokeStatic<object>("NormalizeIterationLogCriteria", new WorkIterationLogCriteria(Take: 999))).Take);
        Assert.NotNull(InvokeStatic<object>("NormalizeWorkerIterationOverviewCriteria", (object?)null));

        var normalizeEnums = typeof(WorkableViewQueryAdapter)
            .GetMethod("NormalizeWorkerOverviewEnumFilters", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(LogLevel));
        Assert.Null(normalizeEnums.Invoke(null, [null]));
        Assert.Null(normalizeEnums.Invoke(null, [Array.Empty<LogLevel>()]));
        Assert.Equal(
            [LogLevel.Warning, LogLevel.Error],
            Assert.IsAssignableFrom<IReadOnlyList<LogLevel>>(normalizeEnums.Invoke(
                null,
                [new[] { LogLevel.Warning, LogLevel.Warning, LogLevel.Error }])));

        Assert.Equal([WorkDefinitionMetadataDefaults.Category], InvokeStatic<string[]>("SplitCategoryPath", (object?)null));
        Assert.Equal(["Parent", "Child"], InvokeStatic<string[]>("SplitCategoryPath", " Parent : Child "));

        using var validOptions = JsonDocument.Parse($$"""{"workerId":"{{Guid.NewGuid():D}}"}""");
        using var invalidOptions = JsonDocument.Parse("""{"workerId":"invalid"}""");
        Assert.True(TryGetWorkerId(validOptions.RootElement, out _));
        Assert.False(TryGetWorkerId(invalidOptions.RootElement, out _));
        Assert.False(TryGetWorkerId(null, out _));

        Assert.Equal(7, TryGetInt32(JsonSerializer.SerializeToElement(new { value = 7 }), "value"));
        Assert.Null(TryGetInt32(JsonSerializer.SerializeToElement(new { value = 7.5 }), "value"));
        Assert.Null(TryGetInt32((JsonElement?)null, "value"));
        Assert.Null(TryGetInt32((JsonElement?)default(JsonElement), "value"));
    }

    private static T InvokeStatic<T>(string name, params object?[] arguments)
    {
        var candidates = typeof(WorkableViewQueryAdapter)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .ToArray();
        var method = Assert.Single(candidates);
        return (T)method.Invoke(null, arguments)!;
    }

    private static WorkerActivityEvent Activity(
        string id,
        WorkerActivityEventKind kind,
        long? sequence,
        WorkerState? state,
        WorkerIterationFailure? failure,
        DateTimeOffset now)
        => new(
            id,
            now,
            kind,
            failure is null ? WorkerActivityEventCategory.SystemEvent : WorkerActivityEventCategory.Failure,
            null,
            null,
            null,
            state,
            sequence,
            sequence is null ? null : WorkCompletionStatus.Failed,
            null,
            null,
            null,
            failure);

    private static WorkerIterationSnapshot CreateIteration(
        long sequence,
        WorkCompletionStatus status,
        WorkerIterationFailure? failure,
        DateTimeOffset now)
        => new(
            sequence,
            now,
            now,
            TimeSpan.Zero,
            status,
            null,
            failure is null
                ? []
                : [WorkMessage.Error(failure.Code ?? "failed", failure.Message)]);

    private static WorkerSnapshot CreateWorker(WorkerState state, DateTimeOffset now)
        => new(
            WorkerId.New(),
            Revision: 1,
            StateSequence: 1,
            DefinitionName: "timeline.branch",
            DefinitionCategory: "Tests",
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            RequestContext: WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            State: state,
            Input: null,
            Output: null,
            Options: WorkerOptions.Default,
            Configuration: WorkConfiguration.Default,
            Messages: [],
            InterruptionReason: null,
            CreatedAt: now,
            StateChangedAt: now,
            UpdatedAt: now)
        {
            RetryAttempt = state == WorkerState.Retrying ? 1 : 0,
            NextRunAt = state == WorkerState.Retrying ? now.AddMinutes(1) : null,
        };

    private static T InvokeInstance<T>(object instance, string name, params object?[] arguments)
        => (T)instance.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(instance, arguments)!;

    private static bool TryGetWorkerId(JsonElement? options, out WorkerId workerId)
    {
        var method = typeof(WorkableViewQueryAdapter).GetMethod(
            "TryGetWorkerId",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] arguments = [options, null];
        var result = Assert.IsType<bool>(method.Invoke(null, arguments));
        workerId = Assert.IsType<WorkerId>(arguments[1]);
        return result;
    }

    private static int? TryGetInt32(JsonElement? options, string propertyName)
    {
        var method = typeof(WorkableViewQueryAdapter).GetMethod(
            "TryGetInt32",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(JsonElement?), typeof(string)],
            modifiers: null);
        Assert.NotNull(method);
        return (int?)method.Invoke(null, [options, propertyName]);
    }
}
