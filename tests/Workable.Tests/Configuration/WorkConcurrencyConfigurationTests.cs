using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Concurrency")]
public sealed class WorkConcurrencyConfigurationTests
{
    private const int TestMaximumCapacity = 7;

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
        Assert.Equal(WorkCoordinationStorage.Local, WorkCoordinationConfiguration.Default.Storage);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var concurrency = FullConcurrencyConfiguration();
        var definition = WorkDefinition.Create("concurrent", "Has concurrency configuration.",
            configuration: WorkConfiguration.Default with
            {
                Coordination = CoordinationWithConcurrency(concurrency),
            });

        AssertConcurrency(concurrency, definition.Configuration.Coordination.Concurrency);
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

        AssertConcurrency(FullConcurrencyConfiguration(), configured.Configuration.Coordination.Concurrency);
    }

    [Fact]
    public void AttributeCanSetPersistenceBackedConcurrencyWhenDurabilityIsEnabled()
    {
        var definition = WorkDefinition.Create("persistent-concurrency-attribute", "Uses persistent concurrency.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<PersistentAttributedConcurrencyWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "persistent-concurrency-attribute");

        Assert.True(configured.Configuration.Coordination.Durability.IsEnabled);
        Assert.True(configured.Configuration.Coordination.Idempotency.IsEnabled);
        Assert.Equal(WorkCoordinationStorage.Persistent, configured.Configuration.Coordination.Storage);
        Assert.True(configured.Configuration.Coordination.Concurrency.IsEnabled);
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

        Assert.True(configured.Configuration.Coordination.Concurrency.IsEnabled);
        Assert.Equal(2, configured.Configuration.Coordination.Concurrency.MaximumCapacity);
        Assert.Equal(WorkConcurrencyLimitReachedBehavior.Ignore, configured.Configuration.Coordination.Concurrency.LimitReachedBehavior);
        Assert.Equal(WorkConcurrencyOverrideBehavior.Flexible, configured.Configuration.Coordination.Concurrency.OverrideBehavior);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("concurrency-queue-override", "Queue options override definition concurrency configuration.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
                Coordination = CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = 1,
                }),
            });
        var system = CreateSystem(definition, ExecuteSuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "concurrency-queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    Coordination = CoordinationWithConcurrency(FullConcurrencyConfiguration()),
                }));
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        AssertConcurrency(FullConcurrencyConfiguration(), worker.Configuration.Coordination.Concurrency);
    }

    [Fact]
    public async Task QueueOptionsWithInvalidConfigurationReturnInvalidOutcome()
    {
        var definition = WorkDefinition.Create("invalid-concurrency-queue", "Queue override is invalid.");
        var system = CreateSystem(definition, ExecuteSuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "invalid-concurrency-queue",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Coordination = CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
                    {
                        IsEnabled = true,
                    }),
                }));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.configuration.concurrency.maximum_capacity_required" &&
            message.Target == "configuration.coordination.concurrency.maximumCapacity");
    }

    [Fact]
    public async Task RuntimeReconfigurationCanUpdateConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-concurrency", "Can change concurrency configuration while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, ExecuteSuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-concurrency");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Coordination: CoordinationWithConcurrency(FullConcurrencyConfiguration())));

        Assert.True(outcome.IsAccepted);
        AssertConcurrency(FullConcurrencyConfiguration(), RequiredWorker(outcome.Worker).Configuration.Coordination.Concurrency);
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsInvalidConfiguration()
    {
        var definition = WorkDefinition.Create("runtime-invalid-concurrency", "Rejects invalid concurrency config while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, ExecuteSuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-invalid-concurrency");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Coordination: CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
            })));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.concurrency.maximum_capacity_required" &&
            message.Target == "configuration.coordination.concurrency.maximumCapacity");
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsSubjectScopeWhenWorkerHasNoSubject()
    {
        var definition = WorkDefinition.Create("runtime-missing-subject", "Rejects subject-scoped concurrency without input subject.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, ExecuteSuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-missing-subject");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Coordination: CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerSubject,
            })));

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
        var system = CreateSystem(definition, ExecuteSuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-missing-key");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Coordination: CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerConcurrencyKey,
            })));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.concurrency.key_required" &&
            message.Target == "input.concurrencyKey");
    }

    [Fact]
    public void ConcurrencyInputValidationIsSharedAndRejectsMissingSubject()
    {
        var messages = WorkConfigurationValidator.ValidateConcurrencyInput(
            coordination: CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerSubject,
            }),
            input: WorkInput.Empty);

        var message = Assert.Single(messages);
        Assert.Equal("workable.concurrency.subject_required", message.Code);
        Assert.Equal("input.subjectId", message.Target);
    }

    [Fact]
    public void ConcurrencyInputValidationIsSharedAndRejectsMissingConcurrencyKey()
    {
        var messages = WorkConfigurationValidator.ValidateConcurrencyInput(
            coordination: CoordinationWithConcurrency(WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerConcurrencyKey,
            }),
            input: WorkInput.Empty);

        var message = Assert.Single(messages);
        Assert.Equal("workable.concurrency.key_required", message.Code);
        Assert.Equal("input.concurrencyKey", message.Target);
    }

    [Theory]
    [MemberData(nameof(LocalConcurrencyPermutations))]
    public void LocalConcurrencyAllowsAllScopeBlockingLimitAndOverrideCombinations(
        WorkConcurrencyScope scope,
        WorkConcurrencyBlockingMode blockingMode,
        WorkConcurrencyLimitReachedBehavior limitReachedBehavior,
        WorkConcurrencyOverrideBehavior overrideBehavior)
    {
        var messages = WorkConfigurationValidator.Validate(WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Local,
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = 1,
                    Scope = scope,
                    BlockingMode = blockingMode,
                    LimitReachedBehavior = limitReachedBehavior,
                    OverrideBehavior = overrideBehavior,
                },
            },
        });

        Assert.Empty(messages);
    }

    [Fact]
    public void PersistenceBackedConcurrencyRequiresDurableQueue()
    {
        var messages = WorkConfigurationValidator.Validate(WorkConfiguration.Default with
        {
            Coordination = PersistentCoordinationWithConcurrency(PersistenceConcurrencyConfiguration()),
        });

        Assert.Contains(messages, message =>
            message.Code == "workable.configuration.concurrency.persistence_requires_durable_queue" &&
            message.Target == "configuration.coordination.durability.isEnabled");
    }

    [Fact]
    public void PersistenceBackedConcurrencyRequiresWhileExecutingBlocking()
    {
        var messages = WorkConfigurationValidator.Validate(WorkConfiguration.Default with
        {
            Coordination = PersistentCoordinationWithConcurrency(PersistenceConcurrencyConfiguration() with
            {
                BlockingMode = WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
            }, durabilityEnabled: true),
        });

        Assert.Contains(messages, message =>
            message.Code == "workable.configuration.concurrency.persistence_blocking_mode_not_supported" &&
            message.Target == "configuration.coordination.concurrency.blockingMode");
    }

    [Fact]
    public void PersistenceBackedConcurrencyRequiresDeferredStart()
    {
        var messages = WorkConfigurationValidator.Validate(WorkConfiguration.Default with
        {
            Coordination = PersistentCoordinationWithConcurrency(PersistenceConcurrencyConfiguration() with
            {
                LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.Ignore,
            }, durabilityEnabled: true),
        });

        Assert.Contains(messages, message =>
            message.Code == "workable.configuration.concurrency.persistence_requires_deferred_start" &&
            message.Target == "configuration.coordination.concurrency.limitReachedBehavior");
    }

    private static WorkConcurrencyConfiguration FullConcurrencyConfiguration()
        => new()
        {
            IsEnabled = true,
            MaximumCapacity = TestMaximumCapacity,
            Scope = WorkConcurrencyScope.PerDefinition,
            BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
            LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
            OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
        };

    private static WorkConcurrencyConfiguration PersistenceConcurrencyConfiguration()
        => WorkConcurrencyConfiguration.Default with
        {
            IsEnabled = true,
            MaximumCapacity = 1,
            BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
            LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
        };

    public static IEnumerable<object[]> LocalConcurrencyPermutations()
    {
        foreach (var scope in Enum.GetValues<WorkConcurrencyScope>())
        foreach (var blockingMode in Enum.GetValues<WorkConcurrencyBlockingMode>())
        foreach (var limitReachedBehavior in Enum.GetValues<WorkConcurrencyLimitReachedBehavior>())
        foreach (var overrideBehavior in Enum.GetValues<WorkConcurrencyOverrideBehavior>())
        {
            yield return [scope, blockingMode, limitReachedBehavior, overrideBehavior];
        }
    }

    private static WorkCoordinationConfiguration CoordinationWithConcurrency(WorkConcurrencyConfiguration concurrency)
        => WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Concurrency = concurrency,
        };

    private static WorkCoordinationConfiguration PersistentCoordinationWithConcurrency(
        WorkConcurrencyConfiguration concurrency,
        bool durabilityEnabled = false)
        => WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Storage = WorkCoordinationStorage.Persistent,
            Concurrency = concurrency,
            Durability = WorkQueueDurabilityConfiguration.Default with
            {
                IsEnabled = durabilityEnabled,
            },
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

    private static Task<WorkExecutionResult> ExecuteSuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkConcurrency(
        isEnabled: true,
        maximumCapacity: TestMaximumCapacity,
        scope: WorkConcurrencyScope.PerDefinition,
        blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
        limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
        overrideBehavior: WorkConcurrencyOverrideBehavior.Strict)]
    private sealed class FullAttributedConcurrencyWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    [WorkQueueDurability]
    [WorkIdempotency]
    [WorkConcurrency(
        isEnabled: true,
        maximumCapacity: 1,
        blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
        limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart)]
    private sealed class PersistentAttributedConcurrencyWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
