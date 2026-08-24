using System.Reflection;
using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Mcp")]
public sealed class WorkableMcpToolRouterParsingShould
{
    [Fact]
    public void AdvertiseOnlyIndividuallyAuthorizedActionTools()
    {
        Assert.Empty(CreateActionTools(new(false, false, false, false, false, false, false, false, false, false)));

        var advertised = new HashSet<string>(StringComparer.Ordinal);
        for (var capability = 0; capability < 10; capability++)
        {
            var tools = CreateActionTools(CreateAccess(capability));
            Assert.NotEmpty(tools);
            foreach (var tool in tools)
            {
                advertised.Add(tool.ToolName);
            }
        }

        var all = CreateActionTools(new(true, true, true, true, true, true, true, true, true, true));
        Assert.Equal(all.Select(tool => tool.ToolName).ToHashSet(StringComparer.Ordinal), advertised);
        Assert.All(all, tool => Assert.Equal(WorkableMcpServerToolKind.Action, tool.Kind));
    }

    [Fact]
    public void ParseBooleanNumericGuidAndEnumArgumentShapesFailClosed()
    {
        var id = Guid.NewGuid();
        var arguments = Json("""
        {
          "trueValue": true,
          "falseValue": false,
          "stringTrue": "true",
          "stringInvalid": "yes",
          "number": 42,
          "fraction": 1.5,
          "long": 9223372036854775807,
          "overflow": 18446744073709551615,
          "id": "ID_VALUE",
          "invalidId": "not-a-guid",
          "state": "waiting",
          "invalidState": "unknown",
          "completion": "completed",
          "invalidCompletion": "unknown"
        }
        """.Replace("ID_VALUE", id.ToString("D"), StringComparison.Ordinal));

        Assert.True(Invoke<bool?>("ReadBool", arguments, "trueValue"));
        Assert.False(Invoke<bool?>("ReadBool", arguments, "falseValue"));
        Assert.True(Invoke<bool?>("ReadBool", arguments, "stringTrue"));
        Assert.Null(Invoke<bool?>("ReadBool", arguments, "stringInvalid"));
        Assert.Null(Invoke<bool?>("ReadBool", arguments, "number"));
        Assert.Null(Invoke<bool?>("ReadBool", arguments, "missing"));

        Assert.Equal(42, Invoke<int?>("ReadInt", arguments, "number"));
        Assert.Null(Invoke<int?>("ReadInt", arguments, "fraction"));
        Assert.Null(Invoke<int?>("ReadInt", arguments, "missing"));
        Assert.Equal(long.MaxValue, Invoke<long>("ReadRequiredLong", arguments, "long"));
        Assert.Throws<TargetInvocationException>(() => Invoke<long>("ReadRequiredLong", arguments, "overflow"));
        Assert.Throws<TargetInvocationException>(() => Invoke<long>("ReadRequiredLong", arguments, "missing"));

        Assert.Equal(id, Invoke<Guid?>("ReadGuid", arguments, "id"));
        Assert.Null(Invoke<Guid?>("ReadGuid", arguments, "invalidId"));
        Assert.Equal(WorkerState.Waiting, Invoke<WorkerState?>("TryParseWorkerState", "waiting"));
        Assert.Null(Invoke<WorkerState?>("TryParseWorkerState", "unknown"));
        Assert.Null(Invoke<WorkerState?>("TryParseWorkerState", (object?)null));
        Assert.Equal(WorkCompletionStatus.Completed, Invoke<WorkCompletionStatus?>("TryParseCompletionStatus", "completed"));
        Assert.Null(Invoke<WorkCompletionStatus?>("TryParseCompletionStatus", "unknown"));
    }

    [Fact]
    public void BuildWorkerCriteriaFromEmptyAndCompleteArgumentSets()
    {
        var emptyWorkers = Invoke<WorkerCriteria>("ToWorkerCriteria", (object?)null);
        var emptyIterations = Invoke<WorkerIterationCriteria>("ToWorkerIterationCriteria", (object?)null);
        Assert.Null(emptyWorkers.Configuration);
        Assert.Equal(0, emptyWorkers.Skip);
        Assert.Equal(100, emptyWorkers.Take);
        Assert.Equal(WorkerIterationCriteria.DefaultTake, emptyIterations.Take);

        var workerId = Guid.NewGuid();
        var full = Json("""
        {
          "definitionName": "orders.run",
          "subjectType": "order",
          "subjectValue": "100",
          "concurrencyKeyType": "account",
          "concurrencyKeyValue": "200",
          "identifierType": "customer",
          "identifierValue": "300",
          "states": ["Queued", "Waiting", "invalid"],
          "statuses": ["Completed", "Failed", "invalid"],
          "recurrenceEnabled": true,
          "concurrencyEnabled": false,
          "profilingEnabled": "true",
          "createdFrom": "2026-01-01T00:00:00Z",
          "createdTo": "invalid",
          "sort": "UpdatedAt",
          "direction": "Ascending",
          "skip": 2,
          "take": 3,
          "category": "Orders",
          "includeSubcategories": false,
          "workerId": "WORKER_ID"
        }
        """.Replace("WORKER_ID", workerId.ToString("D"), StringComparison.Ordinal));
        var workers = Invoke<WorkerCriteria>("ToWorkerCriteria", full);
        var iterations = Invoke<WorkerIterationCriteria>("ToWorkerIterationCriteria", full);

        Assert.Equal("orders.run", workers.DefinitionName);
        Assert.Equal(new WorkSubjectId("order", "100"), workers.SubjectId);
        Assert.Equal(new WorkConcurrencyKey("account", "200"), workers.ConcurrencyKey);
        Assert.Equal(new WorkIdentifier("customer", "300"), workers.Identifier);
        Assert.Equal(new HashSet<WorkerState> { WorkerState.Queued, WorkerState.Waiting }, workers.States);
        Assert.NotNull(workers.Configuration);
        Assert.True(workers.Configuration.RecurrenceEnabled);
        Assert.False(workers.Configuration.ConcurrencyEnabled);
        Assert.True(workers.Configuration.ProfilingEnabled);
        Assert.Equal(2, workers.Skip);
        Assert.Equal(3, workers.Take);
        Assert.False(workers.IncludeSubcategories);
        Assert.Equal(new WorkerId(workerId), iterations.WorkerId);
        Assert.Equal(
            new HashSet<WorkCompletionStatus>
            {
                WorkCompletionStatus.Completed,
                WorkCompletionStatus.Failed,
                WorkCompletionStatus.Invalid,
            },
            iterations.Statuses);
        Assert.Equal(3, iterations.Take);
    }

    [Fact]
    public void BoundToolNamesDescriptionsAndWrappedInvocationArguments()
    {
        Assert.Equal("short", Invoke<string>("LimitToolName", "short", null));
        Assert.Equal(64, Invoke<string>("LimitToolName", new string('a', 80), null).Length);
        Assert.Equal("base_suffix", Invoke<string>("LimitToolName", "base", "suffix"));
        var suffixed = Invoke<string>("LimitToolName", new string('b', 80), "suffix");
        Assert.Equal(64, suffixed.Length);
        Assert.EndsWith("_suffix", suffixed, StringComparison.Ordinal);

        Assert.Equal("workable_work", WorkableMcpToolRouter.CreateWorkToolName("---"));
        Assert.Equal("workable_work_order_run", WorkableMcpToolRouter.CreateWorkToolName("Order---Run"));

        var uncategorized = new WorkableMcpToolDescriptor(
            "work",
            null,
            " ",
            "{}",
            "application/json",
            null,
            null,
            false,
            null);
        var described = uncategorized with { Description = "Does work.", Category = "Jobs" };
        Assert.Contains("uncategorized", Invoke<string>("CreateWorkToolDescription", uncategorized));
        Assert.Contains("No description", Invoke<string>("CreateWorkToolDescription", uncategorized));
        Assert.Contains("Jobs", Invoke<string>("CreateWorkToolDescription", described));
        Assert.Contains("Does work.", Invoke<string>("CreateWorkToolDescription", described));

        var original = WorkRequestContext.Create(WorkInvocationChannel.Mcp);
        Assert.Same(original, Invoke<WorkRequestContext>("WithDescription", original, " "));
        Assert.Equal("reason", Invoke<WorkRequestContext>("WithDescription", original, "reason").Description);

        var direct = Json("""{"value":1}""");
        var wrapped = Json("""{"input":{"value":2},"description":"reason"}""");
        var wrappedNull = Json("""{"input":null,"description":"reason"}""");
        var missingDescription = Json("""{"input":{"value":3}}""");
        Assert.Null(Invoke<string?>("ReadWorkToolInvocationDescription", direct));
        Assert.Equal("reason", Invoke<string?>("ReadWorkToolInvocationDescription", wrapped));
        Assert.Equal(1, Invoke<JsonElement?>("ReadWorkToolInvocationInput", direct)!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, Invoke<JsonElement?>("ReadWorkToolInvocationInput", wrapped)!.Value.GetProperty("value").GetInt32());
        Assert.Null(Invoke<JsonElement?>("ReadWorkToolInvocationInput", wrappedNull));
        Assert.Equal(JsonValueKind.Object, Invoke<JsonElement?>("ReadWorkToolInvocationInput", missingDescription)!.Value.ValueKind);
    }

    [Fact]
    public void ParseStateCollectionsPairsAndRequiredStringsFailClosed()
    {
        Assert.Null(Invoke<HashSet<WorkerState>?>("ReadStates", Json("""{"states":42}""")));
        Assert.Null(Invoke<HashSet<WorkerState>?>("ReadStates", Json("""{"states":[42,"unknown"]}""")));
        Assert.Equal(
            new HashSet<WorkerState> { WorkerState.Waiting },
            Invoke<HashSet<WorkerState>?>("ReadStates", Json("""{"states":["waiting",42]}""")));

        Assert.Null(Invoke<HashSet<WorkCompletionStatus>?>("ReadCompletionStatuses", Json("""{"statuses":false}""")));
        Assert.Null(Invoke<HashSet<WorkCompletionStatus>?>("ReadCompletionStatuses", Json("""{"statuses":[42,"unknown-value"]}""")));
        Assert.Equal(
            new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed },
            Invoke<HashSet<WorkCompletionStatus>?>("ReadCompletionStatuses", Json("""{"statuses":["completed",false]}""")));

        Assert.Null(Invoke<(string Type, string Value)?>("ReadPair", Json("""{"type":"x"}"""), "type", "value"));
        Assert.Null(Invoke<(string Type, string Value)?>("ReadPair", Json("""{"type":" ","value":"y"}"""), "type", "value"));
        Assert.Equal(
            ("x", "y"),
            Invoke<(string Type, string Value)?>("ReadPair", Json("""{"type":"x","value":"y"}"""), "type", "value"));
        Assert.Equal("value", Invoke<string>("ReadRequiredString", Json("""{"name":"value"}"""), "name"));
        Assert.Throws<TargetInvocationException>(() =>
            Invoke<string>("ReadRequiredString", Json("""{"name":""}"""), "name"));
        Assert.Throws<TargetInvocationException>(() =>
            Invoke<string>("ReadRequiredString", (object?)null, "name"));
    }

    [Fact]
    public void RejectDuplicateAndUnsupportedConfigurationPropertiesAtEveryDepth()
    {
        Invoke<object?>("RejectDuplicatePropertiesRecursively", null, "arguments");
        Invoke<object?>("RejectDuplicatePropertiesRecursively", Json("42"), "arguments");
        Invoke<object?>("RejectDuplicatePropertiesRecursively", Json("""[{"ok":1}]"""), "arguments");
        Assert.Throws<TargetInvocationException>(() => Invoke<object?>(
            "RejectDuplicatePropertiesRecursively",
            Json("""[{"outer":{"Name":1,"name":2}}]"""),
            "arguments"));

        Invoke<object?>("RejectUnsupportedProperties", Json("42"), "arguments", Array.Empty<string>());
        Invoke<object?>("RejectUnsupportedProperties", null, "arguments", Array.Empty<string>());
        Invoke<object?>("RejectUnsupportedProperties", Json("""{"known":1}"""), "arguments", new[] { "KNOWN" });
        Assert.Throws<TargetInvocationException>(() => Invoke<object?>(
            "RejectUnsupportedProperties",
            Json("""{"unknown":1}"""),
            "arguments",
            new[] { "known" }));

        Assert.Throws<TargetInvocationException>(() => Invoke<WorkDefinitionReconfiguration>(
            "DeserializeDefinitionReconfiguration",
            Json("42"),
            "changes"));
        Assert.Throws<TargetInvocationException>(() => Invoke<string>("CreateWorkToolInputSchema", "null"));
    }

    [Fact]
    public void ParseValidDuplicateAndUnsupportedDefinitionReconfigurationObjects()
    {
        var defaults = Invoke<WorkDefinitionReconfiguration>(
            "DeserializeDefinitionReconfiguration",
            Json("""{"defaultOptions":{"profilingEnabled":true}}"""),
            "changes");
        var configurationJson = JsonSerializer.SerializeToElement(new
        {
            configuration = new
            {
                start = WorkStartConfiguration.DoNotStart,
                coordination = WorkConfiguration.Default.Coordination,
                recurrence = WorkConfiguration.Default.Recurrence,
                transientRetry = WorkConfiguration.Default.TransientRetry,
                failedWorker = WorkConfiguration.Default.FailedWorker,
                logging = WorkConfiguration.Default.Logging,
                retention = WorkConfiguration.Default.Retention,
            },
        });
        Assert.True(defaults.DefaultOptions?.ProfilingEnabled);
        Assert.Throws<TargetInvocationException>(() => Invoke<WorkDefinitionReconfiguration>(
            "DeserializeDefinitionReconfiguration",
            configurationJson,
            "changes"));
        Assert.Throws<TargetInvocationException>(() => Invoke<WorkDefinitionReconfiguration>(
            "DeserializeDefinitionReconfiguration",
            Json("""{"defaultOptions":{},"DEFAULTOPTIONS":{}}"""),
            "changes"));
        Assert.Throws<TargetInvocationException>(() => Invoke<WorkDefinitionReconfiguration>(
            "DeserializeDefinitionReconfiguration",
            Json("""{"unsupported":{}}"""),
            "changes"));
        Assert.Throws<TargetInvocationException>(() => Invoke<WorkDefinitionReconfiguration>(
            "DeserializeDefinitionReconfiguration",
            Json("""{"defaultOptions":null}"""),
            "changes"));
    }

    [Fact]
    public void RecognizeEverySupportedAndUnsupportedJsonSchemaContentType()
    {
        Assert.False(InvokeMcpExtension<bool>("HasJsonSchema", new WorkSchema(" ", "application/json")));
        Assert.False(InvokeMcpExtension<bool>("HasJsonSchema", new WorkSchema("{}", "text/plain")));
        Assert.True(InvokeMcpExtension<bool>("HasJsonSchema", new WorkSchema("{}", "application/vendor+json")));
        Assert.True(InvokeMcpExtension<bool>("HasJsonSchema", new WorkSchema("{}", "application/json")));
        Assert.True(InvokeMcpExtension<bool>("HasJsonSchema", new WorkSchema("{}", "application/schema+json")));
    }

    [Theory]
    [InlineData(WorkCompletionStatus.Completed, WorkableMcpInvocationStatus.Completed)]
    [InlineData(WorkCompletionStatus.Interrupted, WorkableMcpInvocationStatus.Interrupted)]
    [InlineData(WorkCompletionStatus.Canceled, WorkableMcpInvocationStatus.Canceled)]
    [InlineData(WorkCompletionStatus.Failed, WorkableMcpInvocationStatus.Failed)]
    [InlineData(WorkCompletionStatus.Paused, WorkableMcpInvocationStatus.Failed)]
    public async Task MapEveryMcpCompletionStatus(
        WorkCompletionStatus completionStatus,
        WorkableMcpInvocationStatus expectedStatus)
    {
        var workerId = WorkerId.New();
        var completion = new WorkCompletion(completionStatus, null, null, []);
        var session = new CompletionSession(new CompletionQueue(workerId, completion));

        var result = await session.InvokeMcpTool(
            "completion",
            options: new WorkableMcpInvocationOptions
            {
                CompletionTimeout = TimeSpan.FromSeconds(5),
            });

        Assert.Equal(expectedStatus, result.Status);
        Assert.Same(completion, result.Completion);
    }

    private static IReadOnlyList<WorkableMcpServerToolDescriptor> CreateActionTools(WorkOperationAccessSummary access)
        => Invoke<IReadOnlyList<WorkableMcpServerToolDescriptor>>("CreateActionTools", access);

    private static WorkOperationAccessSummary CreateAccess(int enabled)
    {
        bool Is(int index) => enabled == index;
        return new(Is(0), Is(1), Is(2), Is(3), Is(4), Is(5), Is(6), Is(7), Is(8), Is(9));
    }

    private static JsonElement Json(string value)
        => JsonDocument.Parse(value).RootElement.Clone();

    private static T Invoke<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(WorkableMcpToolRouter)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length);
        return (T)method.Invoke(null, arguments)!;
    }

    private static T InvokeMcpExtension<T>(string methodName, params object?[] arguments)
        => (T)typeof(WorkableMcpExtensions)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length)
            .Invoke(null, arguments)!;

    private sealed class CompletionSession(IWorkQueueService queue) : IWorkSystemSession
    {
        public string? SystemName => null;
        public WorkSystemState SystemState => WorkSystemState.Started;
        public WorkSystemCapabilities Capabilities => WorkSystemCapabilities.None;
        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();
        public IWorkCatalog Catalog => throw new NotSupportedException();
        public IWorkQueueService Queue { get; } = queue;
        public IWorkerOperations Workers => throw new NotSupportedException();
        public IWorkQueryService Query => throw new NotSupportedException();
        public IWorkEventStream Events => throw new NotSupportedException();
        public IWorkChangeStream Changes => throw new NotSupportedException();
    }

    private sealed class CompletionQueue(WorkerId workerId, WorkCompletion completion) : IWorkQueueService
    {
        public void NotifyDurableWorkAvailable()
        {
        }

        public Task<IWorkerHandle> Enqueue(
            string name,
            WorkInput? input = null,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IWorkerHandle>(new CompletionHandle(workerId, completion));

        public Task<IWorkerHandle> Enqueue<TInput>(
            string name,
            TInput input,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.Enqueue(name, WorkInput.FromValue(input), options, cancellationToken);
    }

    private sealed class CompletionHandle(WorkerId workerId, WorkCompletion completion) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = WorkQueueOutcome.Accepted(workerId);
        public WorkerId? WorkerId { get; } = workerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => Task.FromResult(completion);

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
    }
}
