using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "AutomaticStart")]
public sealed class AutomaticStartTests
{
    [Fact]
    public async Task WithAutomaticStartQueuesWorkWhenSystemStarts()
    {
        var tracker = new AutomaticStartTracker();
        var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<AutomaticStartExecutor>(
                WorkDefinition.Create("automatic.start"),
                configure => configure.WithAutomaticStart()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await tracker.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var workers = (await system.Query.Workers(new WorkerCriteria())).Workers;
        var worker = Assert.Single(workers);
        var snapshot = await system.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.Equal("automatic.start", worker.DefinitionName);
        Assert.Equal(WorkerState.Completed, worker.State);
        Assert.Contains("automatically started", snapshot.Origin.Description);
    }

    [Fact]
    public async Task WithAutomaticStartQueuesConfiguredInstanceCount()
    {
        var tracker = new AutomaticStartTracker();
        var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<CountingAutomaticStartExecutor>(
                WorkDefinition.Create("automatic.instances"),
                configure => configure.WithAutomaticStart(instanceCount: 3)))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await tracker.WaitForCount(3);

        var workers = (await system.Query.Workers(new WorkerCriteria())).Workers;
        Assert.Equal(3, workers.Count);
        Assert.All(workers, worker => Assert.Equal(WorkerState.Completed, worker.State));
    }

    [Fact]
    public async Task WithAutomaticStartUsesInputFactoryAtSystemStart()
    {
        var tracker = new AutomaticStartTracker();
        var provider = new ServiceCollection()
            .AddSingleton(tracker)
            .AddWorkableSystem(builder => builder.AddWork<AutomaticInputExecutor>(
                WorkDefinition.Create("automatic.input"),
                configure => configure.WithAutomaticStart(() => new AutomaticInput("hello"))))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        var message = await tracker.Message.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("hello", message);
    }

    [Fact]
    public async Task WithAutomaticStartRejectsWorkConfiguredToWaitForCompletion()
    {
        var provider = new ServiceCollection()
            .AddSingleton<AutomaticStartTracker>()
            .AddWorkableSystem(builder => builder.AddWork<AutomaticStartExecutor>(
                WorkDefinition.Create("automatic.wait"),
                configure => configure
                    .ReturnAfterCompleted()
                    .WithAutomaticStart()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Contains(nameof(WorkStartPolicy.StartAndReturnAfterCompleted), exception.Message);
        Assert.Equal(WorkSystemState.Stopped, system.State);
    }

    private sealed record AutomaticInput(string Message);

    private sealed class AutomaticStartTracker
    {
        private int count;
        private TaskCompletionSource<int> countReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete()
        {
            this.Completed.TrySetResult();
            var current = Interlocked.Increment(ref this.count);
            this.countReached.TrySetResult(current);
        }

        public void RecordMessage(string message)
        {
            this.Message.TrySetResult(message);
            this.Complete();
        }

        public async Task WaitForCount(int expected)
        {
            while (Volatile.Read(ref this.count) < expected)
            {
                var currentSignal = this.countReached;
                await currentSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (Volatile.Read(ref this.count) < expected)
                {
                    Interlocked.CompareExchange(
                        ref this.countReached,
                        new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
                        currentSignal);
                }
            }
        }
    }

    private sealed class AutomaticStartExecutor(AutomaticStartTracker tracker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Complete();
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class CountingAutomaticStartExecutor(AutomaticStartTracker tracker) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
        {
            tracker.Complete();
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class AutomaticInputExecutor(AutomaticStartTracker tracker) : IWorkExecutor<AutomaticInput>
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            AutomaticInput input,
            CancellationToken cancellationToken)
        {
            tracker.RecordMessage(input.Message);
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }
}
