using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "TransientRetry")]
public sealed class WorkTransientRetryConfigurationTests
{
    [Fact]
    public void DefaultsMatchConfiguredValues()
    {
        var transientRetry = WorkTransientRetryConfiguration.Default;

        Assert.Equal(0, transientRetry.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(800), transientRetry.InitialDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(500), transientRetry.Jitter);
        Assert.Equal(TimeSpan.FromSeconds(30), transientRetry.MaximumDelay);
        Assert.Equal(WorkRetryBackoff.Exponential, transientRetry.Backoff);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var transientRetry = FullTransientRetryConfiguration();
        var definition = WorkDefinition.Create("transient-retry-config", "Has transient retry configuration.",
            configuration: WorkConfiguration.Default with
            {
                TransientRetry = transientRetry,
            });

        AssertTransientRetry(transientRetry, definition.Configuration.TransientRetry);
    }

    [Fact]
    public void AttributeCanSetAllFeatures()
    {
        var definition = WorkDefinition.Create("transient-retry-attribute", "Uses every transient retry value.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedTransientRetryWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "transient-retry-attribute");

        AssertTransientRetry(FullTransientRetryConfiguration(), configured.Configuration.TransientRetry);
    }

    [Fact]
    public void BootstrapConfigurationOverridesAttributeConfiguration()
    {
        var definition = WorkDefinition.Create("transient-retry-bootstrap-override", "Bootstrap wins.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedTransientRetryWork>(
                definition,
                configuration => configuration.RetryTransientFailures(2, TimeSpan.FromSeconds(4))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "transient-retry-bootstrap-override");

        Assert.Equal(2, configured.Configuration.TransientRetry.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), configured.Configuration.TransientRetry.InitialDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(500), configured.Configuration.TransientRetry.Jitter);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("transient-retry-queue-override", "Queue options override definition transient retry configuration.",
            configuration: WorkConfiguration.Default with
            {
                TransientRetry = WorkTransientRetryConfiguration.Default with
                {
                    Count = 1,
                    InitialDelay = TimeSpan.FromSeconds(1),
                },
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "transient-retry-queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    TransientRetry = WorkTransientRetryConfiguration.Default with
                    {
                        Count = 3,
                        InitialDelay = TimeSpan.FromSeconds(3),
                    },
                }));
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        Assert.Equal(3, worker.Configuration.TransientRetry.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), worker.Configuration.TransientRetry.InitialDelay);
    }

    [Fact]
    public void DefinitionRejectsInvalidRetryValues()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => WorkDefinition.Create(
            "Invalid Transient Retry",
            "Has invalid transient retry configuration.",
            configuration: WorkConfiguration.Default with
            {
                TransientRetry = WorkTransientRetryConfiguration.Default with
                {
                    Count = 1,
                    InitialDelay = TimeSpan.Zero,
                },
            }));

        Assert.Contains("initial delay", exception.Message);
    }

    [Fact]
    public async Task RuntimeReconfigurationCanUpdateConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-transient-retry", "Can change transient retry configuration while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-transient-retry");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(TransientRetry: FullTransientRetryConfiguration()));

        Assert.True(outcome.IsAccepted);
        AssertTransientRetry(FullTransientRetryConfiguration(), RequiredWorker(outcome.Worker).Configuration.TransientRetry);
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsInvalidConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-invalid-transient-retry", "Rejects invalid transient retry config while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-invalid-transient-retry");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(TransientRetry: WorkTransientRetryConfiguration.Default with
            {
                Count = -1,
            }));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.transient_retry.count_negative" &&
            message.Target == "configuration.transientRetry.count");
    }

    private static WorkTransientRetryConfiguration FullTransientRetryConfiguration()
        => new()
        {
            Count = 4,
            InitialDelay = TimeSpan.FromSeconds(2),
            Jitter = TimeSpan.FromMilliseconds(250),
            MaximumDelay = TimeSpan.FromSeconds(20),
            Backoff = WorkRetryBackoff.None,
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

    private static void AssertTransientRetry(WorkTransientRetryConfiguration expected, WorkTransientRetryConfiguration actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.InitialDelay, actual.InitialDelay);
        Assert.Equal(expected.Jitter, actual.Jitter);
        Assert.Equal(expected.MaximumDelay, actual.MaximumDelay);
        Assert.Equal(expected.Backoff, actual.Backoff);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkTransientRetry(
        count: 4,
        initialDelayMilliseconds: 2_000,
        jitterMilliseconds: 250,
        maximumDelayMilliseconds: 20_000,
        backoff: WorkRetryBackoff.None)]
    private sealed class FullAttributedTransientRetryWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
