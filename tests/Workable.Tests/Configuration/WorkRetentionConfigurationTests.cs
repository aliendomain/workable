using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Retention")]
public sealed class WorkRetentionConfigurationTests
{
    [Fact]
    public void DefaultsMatchConfiguredValues()
    {
        var retention = WorkRetentionConfiguration.Default;

        Assert.Equal(TimeSpan.FromMinutes(5), retention.PurgeInterval);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var retention = FullRetentionConfiguration();
        var definition = WorkDefinition.Create("retained", "Has retention configuration.",
            configuration: WorkConfiguration.Default with
            {
                Retention = retention,
            });

        AssertRetention(retention, definition.Configuration.Retention);
    }

    [Fact]
    public void AttributeRejectsConfigurationWithoutPurgeInterval()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WorkRetentionAttribute(purgeIntervalSeconds: 0));

        Assert.Contains("purge interval", exception.Message);
    }

    [Fact]
    public void AttributeCanSetAllFeatures()
    {
        var definition = WorkDefinition.Create("retention-attribute", "Uses every retention value.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedRetentionWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "retention-attribute");

        AssertRetention(FullRetentionConfiguration(), configured.Configuration.Retention);
    }

    [Fact]
    public void BootstrapConfigurationOverridesAttributeConfiguration()
    {
        var definition = WorkDefinition.Create("retention-bootstrap-override", "Bootstrap wins.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedRetentionWork>(
                definition,
                configuration => configuration.ConfigureRetention(TimeSpan.FromMinutes(2))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "retention-bootstrap-override");

        Assert.Equal(TimeSpan.FromMinutes(2), configured.Configuration.Retention.PurgeInterval);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("retention-queue-override", "Queue options override definition retention configuration.",
            configuration: WorkConfiguration.Default with
            {
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMinutes(1),
                },
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "retention-queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    Retention = WorkRetentionConfiguration.Default with
                    {
                        PurgeInterval = TimeSpan.FromMinutes(4),
                    },
                }));
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        Assert.Equal(TimeSpan.FromMinutes(4), worker.Configuration.Retention.PurgeInterval);
    }

    [Fact]
    public async Task QueueOptionsWithInvalidConfigurationReturnInvalidOutcome()
    {
        var definition = WorkDefinition.Create("invalid-retention-queue", "Queue override is invalid.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "invalid-retention-queue",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Retention = WorkRetentionConfiguration.Default with
                    {
                        PurgeInterval = TimeSpan.Zero,
                    },
                }));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.configuration.retention.purge_interval_required" &&
            message.Target == "configuration.retention.purgeInterval");
    }

    [Fact]
    public async Task RuntimeReconfigurationCanUpdateConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-retention", "Can change retention configuration while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-retention");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Retention: FullRetentionConfiguration()));

        Assert.True(outcome.IsAccepted);
        AssertRetention(FullRetentionConfiguration(), RequiredWorker(outcome.Worker).Configuration.Retention);
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsInvalidConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-invalid-retention", "Rejects invalid retention config while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-invalid-retention");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Retention: WorkRetentionConfiguration.Default with
            {
                PurgeInterval = TimeSpan.Zero,
            }));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.retention.purge_interval_required" &&
            message.Target == "configuration.retention.purgeInterval");
    }

    private static WorkRetentionConfiguration FullRetentionConfiguration()
        => new()
        {
            PurgeInterval = TimeSpan.FromSeconds(90),
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

    private static void AssertRetention(WorkRetentionConfiguration expected, WorkRetentionConfiguration actual)
    {
        Assert.Equal(expected.PurgeInterval, actual.PurgeInterval);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkRetention(purgeIntervalSeconds: 90)]
    private sealed class FullAttributedRetentionWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
