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
