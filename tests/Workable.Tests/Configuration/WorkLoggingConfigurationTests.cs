using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Logging")]
public sealed class WorkLoggingConfigurationTests
{
    [Fact]
    public void DefaultsMatchConfiguredValues()
    {
        var logging = WorkLoggingConfiguration.Default;

        Assert.True(logging.IsEnabled);
        Assert.Equal(LogLevel.Information, logging.Level);
        Assert.Equal(100, logging.MaximumBufferedEntries);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var logging = FullLoggingConfiguration();
        var definition = WorkDefinition.Create("logged", "Has logging configuration.",
            configuration: WorkConfiguration.Default with
            {
                Logging = logging,
            });

        AssertLogging(logging, definition.Configuration.Logging);
    }

    [Fact]
    public void AttributeCanSetAllFeatures()
    {
        var definition = WorkDefinition.Create("logging-attribute", "Uses every logging value.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedLoggingWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "logging-attribute");

        AssertLogging(FullLoggingConfiguration(), configured.Configuration.Logging);
    }

    [Fact]
    public void BootstrapConfigurationOverridesAttributeConfiguration()
    {
        var definition = WorkDefinition.Create("logging-bootstrap-override", "Bootstrap wins.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedLoggingWork>(
                definition,
                configuration => configuration.ConfigureLogging(
                    isEnabled: true,
                    level: LogLevel.Warning,
                    maximumBufferedEntries: 25)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "logging-bootstrap-override");

        Assert.True(configured.Configuration.Logging.IsEnabled);
        Assert.Equal(LogLevel.Warning, configured.Configuration.Logging.Level);
        Assert.Equal(25, configured.Configuration.Logging.MaximumBufferedEntries);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("logging-queue-override", "Queue options override definition logging configuration.",
            configuration: WorkConfiguration.Default with
            {
                Logging = WorkLoggingConfiguration.Default with
                {
                    IsEnabled = false,
                    Level = LogLevel.Debug,
                },
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "logging-queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    Logging = WorkLoggingConfiguration.Default with
                    {
                        IsEnabled = true,
                        Level = LogLevel.Error,
                        MaximumBufferedEntries = 5,
                    },
                }));
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        Assert.True(worker.Configuration.Logging.IsEnabled);
        Assert.Equal(LogLevel.Error, worker.Configuration.Logging.Level);
        Assert.Equal(5, worker.Configuration.Logging.MaximumBufferedEntries);
    }

    [Fact]
    public async Task RuntimeReconfigurationCanUpdateConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-logging", "Can change logging configuration while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-logging");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Logging: FullLoggingConfiguration()));

        Assert.True(outcome.IsAccepted);
        AssertLogging(FullLoggingConfiguration(), RequiredWorker(outcome.Worker).Configuration.Logging);
    }

    [Fact]
    public async Task QueueOptionsRejectInvalidMaximumBufferedEntries()
    {
        var definition = WorkDefinition.Create("invalid-logging", "Invalid logging configuration is rejected.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "invalid-logging",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Logging = WorkLoggingConfiguration.Default with
                    {
                        MaximumBufferedEntries = -1,
                    },
                }));

        Assert.False(handle.QueueOutcome.IsAccepted);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Code == "workable.configuration.logging.maximum_buffered_entries_negative");
    }

    private static WorkLoggingConfiguration FullLoggingConfiguration()
        => new()
        {
            IsEnabled = false,
            Level = LogLevel.Trace,
            MaximumBufferedEntries = 7,
        };

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected the queue to accept a worker.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    private static void AssertLogging(WorkLoggingConfiguration expected, WorkLoggingConfiguration actual)
    {
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.Level, actual.Level);
        Assert.Equal(expected.MaximumBufferedEntries, actual.MaximumBufferedEntries);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkLogging(
        isEnabled: false,
        level: LogLevel.Trace,
        maximumBufferedEntries: 7)]
    private sealed class FullAttributedLoggingWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
