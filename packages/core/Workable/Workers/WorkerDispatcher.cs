using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Workable;
internal sealed class WorkerDispatcher(Func<WorkerRecord, CancellationToken, Task> dispatch) : IDisposable
{
    private readonly Channel<WorkerRecord> scheduledWorkers = Channel.CreateUnbounded<WorkerRecord>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Lock sync = new();
    private CancellationTokenSource? cancellation;
    private Task<DispatcherCompletion>? dispatchTask;

    public void Start(CancellationToken cancellationToken)
    {
        lock (this.sync)
        {
            if (this.dispatchTask is { IsCompleted: false })
            {
                return;
            }

            this.cancellation?.Dispose();
            this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using (ExecutionContext.SuppressFlow())
            {
                this.dispatchTask = Task.Run(() => this.Run(this.cancellation.Token), CancellationToken.None);
            }
        }
    }

    public void Schedule(WorkerRecord worker)
        => this.scheduledWorkers.Writer.TryWrite(worker);

    public void ClearScheduledWork()
    {
        while (this.scheduledWorkers.Reader.TryRead(out _))
        {
            continue;
        }
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        Task<DispatcherCompletion>? task;
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            task = this.dispatchTask;
        }

        if (task is null)
        {
            return;
        }

        await WaitForDispatcherCompletion(task, cancellationToken);
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            this.cancellation?.Dispose();
            this.cancellation = null;
        }
    }

    private async Task<DispatcherCompletion> Run(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var worker in this.scheduledWorkers.Reader.ReadAllAsync(cancellationToken))
            {
                await dispatch(worker, cancellationToken);
            }

            return DispatcherCompletion.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DispatcherCompletion.ShutdownCanceled;
        }
    }

    private static async Task<DispatcherCompletion> WaitForDispatcherCompletion(
        Task<DispatcherCompletion> task,
        CancellationToken cancellationToken)
    {
        if (!task.IsCompleted)
        {
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    if (state is TaskCompletionSource cancellation)
                    {
                        cancellation.TrySetResult();
                    }
                },
                cancellation);

            if (await Task.WhenAny(task, cancellation.Task) != task)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return await task;
    }

    private enum DispatcherCompletion
    {
        Completed,
        ShutdownCanceled,
    }
}
