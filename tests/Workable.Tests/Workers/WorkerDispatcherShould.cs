using System.Runtime.CompilerServices;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workers")]
public sealed class WorkerDispatcherShould
{
    [Fact]
    public async Task StopBeforeStartAndDispatchScheduledWorkOnlyOnceAcrossRepeatedStarts()
    {
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var dispatcher = new WorkerDispatcher((_, _) =>
        {
            Interlocked.Increment(ref calls);
            dispatched.TrySetResult();
            return Task.CompletedTask;
        });

        await dispatcher.Stop(CancellationToken.None);
        dispatcher.Start(CancellationToken.None);
        dispatcher.Start(CancellationToken.None);
        dispatcher.Schedule(UninitializedWorker());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.Stop(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task HonorStopCancellationWhileAnActiveDispatchFinishesLater()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcher = new WorkerDispatcher(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        dispatcher.Start(CancellationToken.None);
        dispatcher.Schedule(UninitializedWorker());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.Stop(cancellation.Token));

        release.TrySetResult();
        await dispatcher.Stop(CancellationToken.None);
    }

    private static WorkerRecord UninitializedWorker()
        => (WorkerRecord)RuntimeHelpers.GetUninitializedObject(typeof(WorkerRecord));
}
