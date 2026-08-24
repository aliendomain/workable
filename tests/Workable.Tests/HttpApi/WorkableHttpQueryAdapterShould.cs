using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpQueryAdapterShould
{
    [Fact]
    public async Task ReturnDefinitionInfoWithQueueRequestSchemaByName()
    {
        var definition = WorkDefinition.Create("http.query.adapter.info");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await Session(system);
        var adapter = new WorkableHttpQueryAdapter();

        var byName = await adapter.DefinitionInfo(session, system, definition.Name);

        Assert.NotNull(byName);
        Assert.Equal(definition.Id, byName.Definition.Id);
        Assert.NotNull(byName.QueueRequestSchema.Schema.JsonSchema);
        Assert.Contains(byName.QueueRequestSchema.Tabs, tab => tab.Id == "queue");
    }

    [Fact]
    public async Task ReturnWorkerConfigurationWithInputDefinitionInfoAndQueueRequestSchema()
    {
        var definition = WorkDefinition.Create(
            "http.query.adapter.configuration",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await Session(system);
        var input = WorkInput
            .FromJson("""{"case":"configuration"}""")
            .WithSubject(new WorkSubjectId("claim", "CLM-1"))
            .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west"));
        var handle = await session.Queue.Enqueue(
            definition.Name,
            input,
            new WorkerOptions(ProfilingEnabled: true));
        var adapter = new WorkableHttpQueryAdapter();

        var configuration = await adapter.WorkerConfiguration(session, system, RequiredWorkerId(handle));

        Assert.NotNull(configuration);
        Assert.True(configuration.ProfilingEnabled);
        Assert.Equal(input.Json, configuration.Input?.Json);
        Assert.Equal(input.SubjectId, configuration.SubjectId);
        Assert.Equal(input.ConcurrencyKey, configuration.ConcurrencyKey);
        Assert.Equal(definition.Id, configuration.DefinitionInfo?.Definition.Id);
        Assert.NotNull(configuration.QueueRequestSchema.Schema.JsonSchema);
    }

    [Fact]
    public async Task ReturnWorkerIterationOverviewWithActivitySummaryAndLogs()
    {
        var definition = WorkDefinition.Create("http.query.adapter.iteration");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkSystemCapabilityContributor, TestSqlProfilingCapabilityContributor>();
        services.AddWorkableSystem(builder => builder.AddWork<LoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await Session(system);
        var handle = await session.Queue.Enqueue(
            definition.Name,
            options: new WorkerOptions(ProfilingEnabled: true));
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);
        await TestEventually.ReadModelDrained(system);
        var adapter = new WorkableHttpQueryAdapter();

        var overview = await adapter.WorkerIterationOverview(session, RequiredWorkerId(handle), sequence: 1);
        var summaryOnlyOverview = await adapter.WorkerIterationOverview(
            session,
            RequiredWorkerId(handle),
            sequence: 1,
            new WorkWorkerIterationOverviewCriteria(
                WorkWorkerIterationOverviewActivity.None,
                IncludeInput: false,
                IncludeOutput: false,
                IncludeProfile: false));

        Assert.NotNull(overview);
        Assert.Equal(WorkWorkerIterationOverviewActivity.Logs, overview.Activity);
        Assert.True(overview.Capabilities.SqlProfilingAvailable);
        Assert.Equal(RequiredWorkerId(handle), overview.Worker.WorkerId);
        Assert.Equal(definition.Name, overview.Worker.DefinitionName);
        Assert.Equal(1, overview.Iteration.Sequence);
        Assert.Equal(WorkCompletionStatus.Completed, overview.Iteration.Status);
        Assert.Equal("""{"ok":true}""", overview.Iteration.Output?.Json);
        Assert.NotNull(overview.Iteration.Profile);
        Assert.Equal(1, overview.Messages.Summary.Total);
        Assert.Equal(1, overview.Messages.Summary.Warning);
        Assert.Null(overview.Messages.Page);
        Assert.Equal(1, overview.Logs.Summary.Total);
        Assert.Equal(1, overview.Logs.Summary.Information);
        Assert.Single(overview.Logs.Page?.Items ?? []);

        Assert.NotNull(summaryOnlyOverview);
        Assert.Equal(WorkWorkerIterationOverviewActivity.None, summaryOnlyOverview.Activity);
        Assert.Null(summaryOnlyOverview.Input);
        Assert.Null(summaryOnlyOverview.Iteration.Output);
        Assert.Null(summaryOnlyOverview.Iteration.Profile);
        Assert.Null(summaryOnlyOverview.Messages.Page);
        Assert.Null(summaryOnlyOverview.Logs.Page);
    }

    [Fact]
    public async Task ReturnWorkerIterationOverviewAutoActivityForMessagesAndNone()
    {
        var messageOnlyDefinition = WorkDefinition.Create("http.query.adapter.iteration.messages");
        var quietDefinition = WorkDefinition.Create("http.query.adapter.iteration.none");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.AddWork(messageOnlyDefinition, (context, input, cancellationToken) =>
                Task.FromResult(WorkExecutionResult.Success(messages:
                [
                    WorkMessage.Information(
                        "http.query.adapter.message",
                        "Only a message was captured.")
                ])));
            builder.AddWork(quietDefinition, SuccessfulWork);
        });
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await Session(system);
        var messageOnlyHandle = await session.Queue.Enqueue(messageOnlyDefinition.Name);
        var quietHandle = await session.Queue.Enqueue(quietDefinition.Name);
        Assert.True((await messageOnlyHandle.WaitForCompletion()).IsCompletedSuccessfully);
        Assert.True((await quietHandle.WaitForCompletion()).IsCompletedSuccessfully);
        await TestEventually.ReadModelDrained(system);
        var adapter = new WorkableHttpQueryAdapter();

        var messageOnlyOverview = await adapter.WorkerIterationOverview(session, RequiredWorkerId(messageOnlyHandle), sequence: 1);
        var quietOverview = await adapter.WorkerIterationOverview(session, RequiredWorkerId(quietHandle), sequence: 1);

        Assert.NotNull(messageOnlyOverview);
        Assert.Equal(WorkWorkerIterationOverviewActivity.Messages, messageOnlyOverview.Activity);
        Assert.NotNull(messageOnlyOverview.Messages.Page);
        Assert.Single(messageOnlyOverview.Messages.Page?.Items ?? []);
        Assert.Null(messageOnlyOverview.Logs.Page);

        Assert.NotNull(quietOverview);
        Assert.Equal(WorkWorkerIterationOverviewActivity.None, quietOverview.Activity);
        Assert.Null(quietOverview.Messages.Page);
        Assert.Null(quietOverview.Logs.Page);
    }

    [Fact]
    public async Task ReturnNullWhenWorkerOrIterationIsMissing()
    {
        var definition = WorkDefinition.Create(
            "http.query.adapter.missing",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await Session(system);
        var adapter = new WorkableHttpQueryAdapter();
        var handle = await session.Queue.Enqueue(definition.Name);

        Assert.Null(await adapter.DefinitionInfo(session, system, "http.query.adapter.unknown"));
        Assert.Null(await adapter.WorkerConfiguration(session, system, WorkerId.New()));
        Assert.Null(await adapter.WorkerIterationOverview(session, WorkerId.New(), sequence: 1));
        Assert.Null(await adapter.WorkerIterationOverview(session, RequiredWorkerId(handle), sequence: 99));
    }

    private static ValueTask<IWorkSystemSession> Session(IWorkSystem system)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.HttpApi,
            description: "Use HTTP query adapter test session.");

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed class LoggedExecutor(ILogger<LoggedExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            logger.LogInformation("query adapter log");
            return Task.FromResult(WorkExecutionResult.Success(
                WorkOutput.FromJson("""{"ok":true}"""),
                [WorkMessage.Warning("query.adapter.warning", "Query adapter warning.")]));
        }
    }

    private sealed class TestSqlProfilingCapabilityContributor : IWorkSystemCapabilityContributor
    {
        public void ConfigureCapabilities(WorkSystemCapabilitiesBuilder capabilities)
        {
            ArgumentNullException.ThrowIfNull(capabilities);
            capabilities.SqlProfilingAvailable = true;
        }
    }
}
