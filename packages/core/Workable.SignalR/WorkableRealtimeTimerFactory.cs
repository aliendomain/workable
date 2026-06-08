namespace Workable;

internal interface IWorkableRealtimeTimerFactory
{
    IWorkableRealtimeTimer Create(TimeSpan interval);
}

internal interface IWorkableRealtimeTimer : IDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}

internal sealed class WorkableRealtimeTimerFactory : IWorkableRealtimeTimerFactory
{
    public IWorkableRealtimeTimer Create(TimeSpan interval)
        => new WorkableRealtimeTimer(interval);
}

internal sealed class WorkableRealtimeTimer(TimeSpan interval) : IWorkableRealtimeTimer
{
    private readonly PeriodicTimer timer = new(interval);

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
        => this.timer.WaitForNextTickAsync(cancellationToken);

    public void Dispose()
        => this.timer.Dispose();
}
