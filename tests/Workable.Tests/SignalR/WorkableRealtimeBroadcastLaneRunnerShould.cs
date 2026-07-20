using Microsoft.Extensions.Logging.Abstractions;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeBroadcastLaneRunnerShould
{
    [Fact]
    public async Task RestartFaultedLaneAfterDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var delayCalls = 0;
        var runner = CreateRunner((delay, _) =>
        {
            delayCalls++;
            Assert.Equal(TimeSpan.FromMilliseconds(25), delay);
            return Task.CompletedTask;
        });
        var attempts = 0;

        await runner.Run(
            new TestWorkSystem("test-system"),
            "events",
            async (_, token) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("First lane attempt failed.");
                }

                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellation.Token,
            TimeSpan.FromMilliseconds(25));

        Assert.Equal(2, attempts);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task RestartCompletedLaneAfterDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var delayCalls = 0;
        var runner = CreateRunner((_, _) =>
        {
            delayCalls++;
            return Task.CompletedTask;
        });
        var attempts = 0;

        await runner.Run(
            new TestWorkSystem("test-system"),
            "views",
            async (_, token) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return;
                }

                await cancellation.CancelAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellation.Token);

        Assert.Equal(2, attempts);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task StopWithoutDelayWhenCancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner((_, _) => throw new InvalidOperationException("Delay should not run."));
        var attempts = 0;

        await runner.Run(
            new TestWorkSystem("test-system"),
            "diagnostics",
            async (_, token) =>
            {
                attempts++;
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
            cancellation.Token);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task StopWhenCancellationInterruptsTheRestartDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var delayCalls = 0;
        var runner = CreateRunner(async (_, token) =>
        {
            delayCalls++;
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
        });

        await runner.Run(
            new TestWorkSystem("test-system"),
            "views",
            (_, _) =>
            {
                attempts++;
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(1, attempts);
        Assert.Equal(1, delayCalls);
    }

    [Fact]
    public async Task PropagateCancellationThatDoesNotBelongToTheHostToken()
    {
        using var hostCancellation = new CancellationTokenSource();
        using var unrelatedCancellation = new CancellationTokenSource();
        await unrelatedCancellation.CancelAsync();
        var runner = CreateRunner((_, _) => throw new InvalidOperationException("Delay should not run."));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.Run(
            new TestWorkSystem("test-system"),
            "events",
            (_, _) => Task.FromCanceled(unrelatedCancellation.Token),
            hostCancellation.Token));

        Assert.Equal(unrelatedCancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task UseOneSecondAsTheDefaultRestartDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner(async (delay, token) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(1), delay);
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
        });

        await runner.Run(
            new TestWorkSystem("test-system"),
            "diagnostics",
            (_, _) => Task.CompletedTask,
            cancellation.Token);
    }

    [Fact]
    public void RejectMissingConstructorDependencies()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WorkableRealtimeBroadcastLaneRunner(null!, Task.Delay));
        Assert.Throws<ArgumentNullException>(() =>
            new WorkableRealtimeBroadcastLaneRunner(
                NullLogger<WorkableRealtimeBroadcastLaneRunner>.Instance,
                null!));
    }

    [Fact]
    public async Task RejectMissingRunDependencies()
    {
        var runner = CreateRunner(Task.Delay);
        var system = new TestWorkSystem("test-system");
        Func<IWorkSystem, CancellationToken, Task> broadcast = (_, _) => Task.CompletedTask;

        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.Run(
            null!, "events", broadcast, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.Run(
            system, null!, broadcast, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => runner.Run(
            system, "events", null!, CancellationToken.None));
    }

    private static WorkableRealtimeBroadcastLaneRunner CreateRunner(Func<TimeSpan, CancellationToken, Task> delay)
        => new(NullLogger<WorkableRealtimeBroadcastLaneRunner>.Instance, delay);

    private sealed class TestWorkSystem(string name) : IWorkSystem
    {
        public WorkSystemId Id { get; } = new(Guid.NewGuid());

        public string? Name { get; } = name;

        public bool RequiresAuthorization => false;

        public WorkSystemState State => WorkSystemState.Started;

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue => throw new NotSupportedException();

        public IWorkerOperations Workers => throw new NotSupportedException();

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public WorkSystemAccessSummary DescribeAccess(WorkRequestContext requestContext)
            => throw new NotSupportedException();

        public IWorkSystemSession CreateSession(WorkRequestContext requestContext)
            => throw new NotSupportedException();

        public Task Start(WorkRequestContext requestContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkSystemStopResult> Stop(WorkRequestContext requestContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }
}
