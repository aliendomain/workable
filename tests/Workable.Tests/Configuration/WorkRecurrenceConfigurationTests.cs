using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Recurrence")]
public sealed class WorkRecurrenceConfigurationTests
{
    [Fact]
    public void DefaultsMatchConfiguredValuesAndAreDisabled()
    {
        var recurrence = WorkRecurrenceConfiguration.Default;

        Assert.False(recurrence.IsEnabled);
        Assert.Equal(TimeSpan.Zero, recurrence.Interval);
        Assert.True(recurrence.ContinueAfterFailure);
        Assert.Equal(3, recurrence.CircuitBreakerFailureThreshold);
        Assert.Equal(25, recurrence.MaximumSuccessfulIterations);
        Assert.Equal(5, recurrence.MaximumFailedIterations);
        Assert.True(recurrence.RaiseCircuitBreakerOpenedEvent);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var recurrence = FullRecurrenceConfiguration();
        var definition = WorkDefinition.Create("configured", "Has configuration.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = recurrence,
            });

        AssertRecurrence(recurrence, definition.Configuration.Recurrence);
    }

    [Fact]
    public void DefinitionRejectsEnabledConfigurationWithoutInterval()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => WorkDefinition.Create(
            "Invalid",
            "Has invalid recurrence.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = new WorkRecurrenceConfiguration
                {
                    IsEnabled = true,
                },
            }));

        Assert.Contains("recurrence interval", exception.Message);
    }

    [Fact]
    public void AttributeRejectsEnabledConfigurationWithoutInterval()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new WorkRecurrenceAttribute(0));

        Assert.Contains("recurrence interval", exception.Message);
    }

    [Fact]
    public void ExecutorAttributeConfiguresRecurrence()
    {
        var definition = WorkDefinition.Create("attributed", "Uses an attribute.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedRecurringWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "attributed");

        Assert.True(configured.Configuration.Recurrence.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(2), configured.Configuration.Recurrence.Interval);
    }

    [Fact]
    public void AttributeCanSetAllFeatures()
    {
        var definition = WorkDefinition.Create("full-attribute", "Uses every recurrence value.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedRecurringWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "full-attribute");

        AssertRecurrence(FullRecurrenceConfiguration(), configured.Configuration.Recurrence);
    }

    [Fact]
    public void BootstrapConfigurationOverridesAttributeConfiguration()
    {
        var definition = WorkDefinition.Create("bootstrap-override", "Bootstrap wins.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedRecurringWork>(
                definition,
                configuration => configuration.RecurEvery(TimeSpan.FromSeconds(5))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "bootstrap-override");

        Assert.True(configured.Configuration.Recurrence.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(5), configured.Configuration.Recurrence.Interval);
    }

    [Fact]
    public void BootstrapConfigurationRejectsEnabledConfigurationWithoutInterval()
    {
        var definition = WorkDefinition.Create("invalid-bootstrap", "Has invalid bootstrap config.");

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configuration => configuration.UseRecurrence(new WorkRecurrenceConfiguration
                {
                    IsEnabled = true,
                }))));

        Assert.Contains("recurrence interval", exception.Message);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("queue-override", "Queue options override definition configuration.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(2)),
                }));
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        Assert.Equal(TimeSpan.FromMinutes(2), worker.Configuration.Recurrence.Interval);
    }

    [Fact]
    public async Task QueueOptionsWithInvalidConfigurationReturnInvalidOutcome()
    {
        var definition = WorkDefinition.Create("invalid-queue", "Queue override is invalid.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "invalid-queue",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Recurrence = new WorkRecurrenceConfiguration
                    {
                        IsEnabled = true,
                    },
                }));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.configuration.recurrence.interval_required" &&
            message.Target == "configuration.recurrence.interval");
    }

    [Fact]
    public async Task RuntimeReconfigurationCanDisableRecurrence()
    {
        var definition = WorkDefinition.Create("runtime-recurrence", "Can change recurrence while queued.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-recurrence");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Recurrence: WorkRecurrenceConfiguration.Disabled));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(1, outcome.Worker?.Revision);
        Assert.False(outcome.Worker?.Configuration.Recurrence.IsEnabled);
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsEnabledConfigurationWithoutInterval()
    {
        var definition = WorkDefinition.Create("runtime-invalid-recurrence", "Rejects invalid recurrence while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("runtime-invalid-recurrence");
        var worker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(Recurrence: new WorkRecurrenceConfiguration
            {
                IsEnabled = true,
            }));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Equal(0, outcome.Worker?.Revision);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.recurrence.interval_required" &&
            message.Target == "configuration.recurrence.interval");
    }

    private static WorkRecurrenceConfiguration FullRecurrenceConfiguration()
        => new()
        {
            IsEnabled = true,
            Interval = TimeSpan.FromSeconds(12),
            ContinueAfterFailure = false,
            CircuitBreakerFailureThreshold = 7,
            MaximumSuccessfulIterations = 8,
            MaximumFailedIterations = 9,
            RaiseCircuitBreakerOpenedEvent = false,
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

    private static void AssertRecurrence(WorkRecurrenceConfiguration expected, WorkRecurrenceConfiguration actual)
    {
        Assert.Equal(expected.IsEnabled, actual.IsEnabled);
        Assert.Equal(expected.Interval, actual.Interval);
        Assert.Equal(expected.ContinueAfterFailure, actual.ContinueAfterFailure);
        Assert.Equal(expected.CircuitBreakerFailureThreshold, actual.CircuitBreakerFailureThreshold);
        Assert.Equal(expected.MaximumSuccessfulIterations, actual.MaximumSuccessfulIterations);
        Assert.Equal(expected.MaximumFailedIterations, actual.MaximumFailedIterations);
        Assert.Equal(expected.RaiseCircuitBreakerOpenedEvent, actual.RaiseCircuitBreakerOpenedEvent);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkRecurrence(2_000)]
    private sealed class AttributedRecurringWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    [WorkRecurrence(
        intervalMilliseconds: 12_000,
        continueAfterFailure: false,
        circuitBreakerFailureThreshold: 7,
        maximumSuccessfulIterations: 8,
        maximumFailedIterations: 9,
        raiseCircuitBreakerOpenedEvent: false)]
    private sealed class FullAttributedRecurringWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
