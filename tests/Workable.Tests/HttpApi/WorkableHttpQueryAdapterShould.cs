using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpQueryAdapterShould
{
    [Fact]
    public async Task ReturnDefinitionInfoWithQueueRequestSchemaByNameAndId()
    {
        var definition = WorkDefinition.Create("http.query.adapter.info");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = Session(system);
        var adapter = new WorkableHttpQueryAdapter();

        var byName = await adapter.DefinitionInfo(session, system, definition.Name);
        var byId = await adapter.DefinitionInfo(session, system, definition.Id);

        Assert.NotNull(byName);
        Assert.Equal(definition.Id, byName.Definition.Id);
        Assert.NotNull(byName.QueueRequestSchema.Schema.JsonSchema);
        Assert.Contains(byName.QueueRequestSchema.Tabs, tab => tab.Id == "queue");
        Assert.NotNull(byId);
        Assert.Equal(byName.Definition.Id, byId.Definition.Id);
        Assert.Equal(byName.QueueRequestSchema.Schema.JsonSchema, byId.QueueRequestSchema.Schema.JsonSchema);
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
        var session = Session(system);
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
    public async Task ReturnWorkerIterationDetailWithMessagesAndLogs()
    {
        var definition = WorkDefinition.Create("http.query.adapter.iteration");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkableSystem(builder => builder.AddWork<LoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = Session(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);
        await TestEventually.ReadModelDrained(system);
        var adapter = new WorkableHttpQueryAdapter();

        var detail = await adapter.WorkerIterationDetail(session, RequiredWorkerId(handle), sequence: 1);

        Assert.NotNull(detail);
        Assert.Equal(RequiredWorkerId(handle), detail.WorkerId);
        Assert.Equal(definition.Id, detail.DefinitionId);
        Assert.Equal(definition.Name, detail.DefinitionName);
        Assert.Equal(1, detail.Iteration.Sequence);
        Assert.Equal(WorkCompletionStatus.Completed, detail.Iteration.Status);
        Assert.Equal("""{"ok":true}""", detail.Iteration.Output?.Json);
        Assert.Equal(1, detail.MessageSummary.Total);
        Assert.Equal(1, detail.MessageSummary.Warning);
        Assert.Equal(1, detail.Logs.Summary.Total);
        Assert.Equal(1, detail.Logs.Summary.Information);
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
        var session = Session(system);
        var adapter = new WorkableHttpQueryAdapter();
        var handle = await session.Queue.Enqueue(definition.Name);

        Assert.Null(await adapter.WorkerConfiguration(session, system, WorkerId.New()));
        Assert.Null(await adapter.WorkerIterationDetail(session, WorkerId.New(), sequence: 1));
        Assert.Null(await adapter.WorkerIterationDetail(session, RequiredWorkerId(handle), sequence: 99));
    }

    private static IWorkSystemSession Session(IWorkSystem system)
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
}
