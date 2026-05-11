using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Invocation")]
public sealed class WorkInvocationConfigurationTests
{
    [Fact]
    public void AttributeAddsInvocationChannels()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedInvocationWork>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var definition = RequiredDefinition(system, "attributed.invocation");

        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.DotNet));
        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi));
        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
    }

    [Fact]
    public void BootstrapAddsInvocationChannels()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("bootstrap.invocation"),
                SuccessfulWork,
                configuration => configuration.AllowInvocationFrom(WorkInvocationChannel.Mcp)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var definition = RequiredDefinition(system, "bootstrap.invocation");

        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.DotNet));
        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi));
        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
    }

    [Fact]
    public void BootstrapAddsInvocationChannelsToCurrentConfiguration()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    "bootstrap.invocation.additive",
                    configuration: WorkConfiguration.Default with
                    {
                        Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.DotNet),
                    }),
                SuccessfulWork,
                configuration => configuration.AllowInvocationFrom(WorkInvocationChannel.Mcp)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var definition = RequiredDefinition(system, "bootstrap.invocation.additive");

        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.DotNet));
        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
        Assert.False(definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi));
    }

    [Fact]
    public void UseInvocationReplacesInvocationChannels()
    {
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("bootstrap.invocation.replace"),
                SuccessfulWork,
                configuration => configuration.UseInvocation(
                    WorkInvocationConfiguration.Allow(WorkInvocationChannel.Mcp))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var definition = RequiredDefinition(system, "bootstrap.invocation.replace");

        Assert.True(definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
        Assert.False(definition.Configuration.Invocation.Allows(WorkInvocationChannel.DotNet));
        Assert.False(definition.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi));
    }

    [Fact]
    public async Task QueueOptionsCannotOverrideDefinitionInvocationChannels()
    {
        var definition = WorkDefinition.Create("queue.override.invocation");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var handle = await system.Queue.Enqueue(
            "queue.override.invocation",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.Mcp),
                }));
        var worker = await system.Query.GetWorker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

        Assert.NotNull(worker);
        Assert.False(worker.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
        Assert.True(worker.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi));
    }

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkMetadata("attributed.invocation", "Configuration")]
    [WorkInvocation(WorkInvocationChannel.Mcp)]
    private sealed class AttributedInvocationWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
