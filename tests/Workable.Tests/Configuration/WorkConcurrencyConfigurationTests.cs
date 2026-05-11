using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Concurrency")]
public sealed class WorkConcurrencyConfigurationTests
{
    [Fact]
    public void DefaultsMatchConfiguredValuesAndAreDisabled()
    {
        var concurrency = WorkConcurrencyConfiguration.Default;

        Assert.False(concurrency.IsEnabled);
        Assert.Equal(0, concurrency.MaximumCapacity);
        Assert.Equal(WorkConcurrencyScope.PerDefinition, concurrency.Scope);
        Assert.Equal(WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed, concurrency.BlockingMode);
        Assert.Equal(WorkConcurrencyLimitReachedBehavior.Ignore, concurrency.LimitReachedBehavior);
        Assert.Equal(WorkConcurrencyOverrideBehavior.Flexible, concurrency.OverrideBehavior);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var concurrency = FullConcurrencyConfiguration();
        var definition = WorkDefinition.Create("concurrent", "Has concurrency configuration.",
            configuration: WorkConfiguration.Default with
            {
                Concurrency = concurrency,
            });

        AssertConcurrency(concurrency, definition.Configuration.Concurrency);
    }

    [Fact]
    public void AttributeRejectsEnabledConcurrencyWithoutCapacity()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WorkConcurrencyAttribute(isEnabled: true));

        Assert.Contains("maximum capacity", exception.Message);
    }

    [Fact]
    public void AttributeCanSetAllFeatures()
    {
        var definition = WorkDefinition.Create("concurrency-attribute", "Uses every concurrency value.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedConcurrencyWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "concurrency-attribute");

        AssertConcurrency(FullConcurrencyConfiguration(), configured.Configuration.Concurrency);
    }

    [Fact]
    public void BootstrapConfigurationOverridesAttributeConfiguration()
    {
        var definition = WorkDefinition.Create("concurrency-bootstrap-override", "Bootstrap wins.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedConcurrencyWork>(
                definition,
                configuration => configuration.LimitConcurrency(
                    2,
                    limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.Ignore,
                    overrideBehavior: WorkConcurrencyOverrideBehavior.Flexible)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "concurrency-bootstrap-override");

        Assert.True(configured.Configuration.Concurrency.IsEnabled);
        Assert.Equal(2, configured.Configuration.Concurrency.MaximumCapacity);
        Assert.Equal(WorkConcurrencyLimitReachedBehavior.Ignore, configured.Configuration.Concurrency.LimitReachedBehavior);
        Assert.Equal(WorkConcurrencyOverrideBehavior.Flexible, configured.Configuration.Concurrency.OverrideBehavior);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("concurrency-queue-override", "Queue options override definition concurrency configuration.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = 1,
                },
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "concurrency-queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    Concurrency = FullConcurrencyConfiguration(),
                }));
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        AssertConcurrency(FullConcurrencyConfiguration(), worker.Configuration.Concurrency);
    }

    [Fact]
    public async Task QueueOptionsWithInvalidConfigurationReturnInvalidOutcome()
    {
        var definition = WorkDefinition.Create("invalid-concurrency-queue", "Queue override is invalid.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "invalid-concurrency-queue",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Concurrency = WorkConcurrencyConfiguration.Default with
                    {
                        IsEnabled = true,
                    },
                }));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.configuration.concurrency.maximum_capacity_required" &&
            message.Target == "configuration.concurrency.maximumCapacity");
    }

    [Fact]
    public async Task RuntimeReconfigurationCanUpdateConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-concurrency", "Can change concurrency configuration while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-concurrency");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Concurrency: FullConcurrencyConfiguration()));

        Assert.True(outcome.IsAccepted);
        AssertConcurrency(FullConcurrencyConfiguration(), RequiredWorker(outcome.Worker).Configuration.Concurrency);
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsInvalidConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-invalid-concurrency", "Rejects invalid concurrency config while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-invalid-concurrency");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Concurrency: WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
            }));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.concurrency.maximum_capacity_required" &&
            message.Target == "configuration.concurrency.maximumCapacity");
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsSubjectScopeWhenWorkerHasNoSubject()
    {
        var definition = WorkDefinition.Create("runtime-missing-subject", "Rejects subject-scoped concurrency without input subject.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-missing-subject");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Concurrency: WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerSubject,
            }));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.concurrency.subject_required" &&
            message.Target == "input.subjectId");
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsConcurrencyKeyScopeWhenWorkerHasNoKey()
    {
        var definition = WorkDefinition.Create("runtime-missing-key", "Rejects key-scoped concurrency without input key.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-missing-key");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Concurrency: WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerConcurrencyKey,
            }));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.concurrency.key_required" &&
            message.Target == "input.concurrencyKey");
    }

    private static WorkConcurrencyConfiguration FullConcurrencyConfiguration()
        => new()
        {
            IsEnabled = true,
            MaximumCapacity = 7,
            Scope = WorkConcurrencyScope.PerDefinition,
            BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
            LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
            OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
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

    private static void AssertConcurrency(WorkConcurrencyConfiguration expected, WorkConcurrencyConfiguration actual)
    {
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.MaximumCapacity, actual.MaximumCapacity);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.BlockingMode, actual.BlockingMode);
        Assert.Equal(expected.LimitReachedBehavior, actual.LimitReachedBehavior);
        Assert.Equal(expected.OverrideBehavior, actual.OverrideBehavior);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkConcurrency(
        isEnabled: true,
        maximumCapacity: 7,
        scope: WorkConcurrencyScope.PerDefinition,
        blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
        limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
        overrideBehavior: WorkConcurrencyOverrideBehavior.Strict)]
    private sealed class FullAttributedConcurrencyWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
