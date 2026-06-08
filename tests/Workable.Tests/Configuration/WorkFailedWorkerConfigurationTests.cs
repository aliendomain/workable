using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Configuration")]
public sealed class WorkFailedWorkerConfigurationTests
{
    [Fact]
    public void DefaultsMatchConfiguredValues()
    {
        var failedWorker = WorkFailedWorkerConfiguration.Default;

        Assert.Equal(WorkFailedWorkerHandling.Manual, failedWorker.Handling);
        Assert.Equal(TimeSpan.FromMinutes(10), failedWorker.AutoCancelAfter);
    }

    [Fact]
    public void WorkDefinitionCanDeclareConfiguration()
    {
        var failedWorker = FullFailedWorkerConfiguration();
        var definition = WorkDefinition.Create("failed-worker-configured", "Has failed-worker handling.",
            configuration: WorkConfiguration.Default with
            {
                FailedWorker = failedWorker,
            });

        AssertFailedWorker(failedWorker, definition.Configuration.FailedWorker);
    }

    [Fact]
    public void DefinitionRejectsRecurringAutoCancelConfiguration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => WorkDefinition.Create(
            "failed-worker-recurring-invalid",
            "Recurring work cannot auto-cancel failed workers.",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
                FailedWorker = FullFailedWorkerConfiguration(),
            }));

        Assert.Contains("not supported for recurring work", exception.Message);
    }

    [Fact]
    public void AttributeCanSetAllFeatures()
    {
        var definition = WorkDefinition.Create("failed-worker-attribute", "Uses every failed-worker value.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedFailedWorkerWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "failed-worker-attribute");

        AssertFailedWorker(FullFailedWorkerConfiguration(), configured.Configuration.FailedWorker);
    }

    [Fact]
    public void BootstrapConfigurationOverridesAttributeConfiguration()
    {
        var definition = WorkDefinition.Create("failed-worker-bootstrap-override", "Bootstrap wins.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<FullAttributedFailedWorkerWork>(
                definition,
                configuration => configuration.ConfigureFailedWorker(
                    WorkFailedWorkerHandling.Manual,
                    TimeSpan.FromMinutes(2))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "failed-worker-bootstrap-override");

        Assert.Equal(WorkFailedWorkerHandling.Manual, configured.Configuration.FailedWorker.Handling);
        Assert.Equal(TimeSpan.FromMinutes(2), configured.Configuration.FailedWorker.AutoCancelAfter);
    }

    [Fact]
    public async Task QueueOptionsOverrideDefinitionConfigurationForWorker()
    {
        var definition = WorkDefinition.Create("failed-worker-queue-override", "Queue options override definition failed-worker handling.",
            configuration: WorkConfiguration.Default with
            {
                FailedWorker = new WorkFailedWorkerConfiguration
                {
                    Handling = WorkFailedWorkerHandling.Manual,
                    AutoCancelAfter = TimeSpan.FromMinutes(9),
                },
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "failed-worker-queue-override",
            options: WorkerOptionFixtures.DoNotStart(
                WorkConfiguration.Default with
                {
                    FailedWorker = FullFailedWorkerConfiguration(),
                }));
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        AssertFailedWorker(FullFailedWorkerConfiguration(), worker.Configuration.FailedWorker);
    }

    [Fact]
    public async Task QueueOptionsWithRecurringAutoCancelConfigurationReturnInvalidOutcome()
    {
        var definition = WorkDefinition.Create("failed-worker-invalid-queue", "Queue override is invalid.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "failed-worker-invalid-queue",
            options: new WorkerOptions(
                Configuration: WorkConfiguration.Default with
                {
                    Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
                    FailedWorker = FullFailedWorkerConfiguration(),
                }));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.configuration.failed_worker.auto_cancel_recurring_not_supported" &&
            message.Target == "configuration.failedWorker.handling");
    }

    [Fact]
    public async Task RuntimeReconfigurationCanUpdateConfiguration()
    {
        var definition = WorkDefinition.Create("failed-worker-runtime", "Can change failed-worker handling while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("failed-worker-runtime");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(FailedWorker: FullFailedWorkerConfiguration()));

        Assert.True(outcome.IsAccepted);
        AssertFailedWorker(FullFailedWorkerConfiguration(), RequiredWorker(outcome.Worker).Configuration.FailedWorker);
    }

    [Fact]
    public async Task RuntimeReconfigurationRejectsRecurringAutoCancelConfiguration()
    {
        var definition = WorkDefinition.Create("failed-worker-runtime-invalid", "Rejects invalid failed-worker handling while queued.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("failed-worker-runtime-invalid");
        var worker = RequiredWorker(await system.Query.Worker(RequiredWorkerId(handle)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(
                Recurrence: WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
                FailedWorker: FullFailedWorkerConfiguration()));

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.configuration.failed_worker.auto_cancel_recurring_not_supported" &&
            message.Target == "configuration.failedWorker.handling");
    }

    private static WorkFailedWorkerConfiguration FullFailedWorkerConfiguration()
        => new()
        {
            Handling = WorkFailedWorkerHandling.AutoCancel,
            AutoCancelAfter = TimeSpan.FromSeconds(90),
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

    private static void AssertFailedWorker(WorkFailedWorkerConfiguration expected, WorkFailedWorkerConfiguration actual)
    {
        Assert.Equal(expected.Handling, actual.Handling);
        Assert.Equal(expected.AutoCancelAfter, actual.AutoCancelAfter);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    [WorkFailedWorker(WorkFailedWorkerHandling.AutoCancel, autoCancelAfterSeconds: 90)]
    private sealed class FullAttributedFailedWorkerWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
