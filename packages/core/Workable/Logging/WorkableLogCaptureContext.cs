using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkableLogCaptureContext : IDisposable
{
    private static readonly AsyncLocal<WorkableLogCaptureContext?> CurrentContext = new();
    private readonly WorkableLogCaptureContext? previous;
    private readonly WorkerRecord worker;
    private readonly WorkerEventPublisher events;
    private readonly WorkExecutionDiagnosticsCoordinator? persistence;
    private bool disposed;

    private WorkableLogCaptureContext(
        WorkerRecord worker,
        WorkerEventPublisher events,
        WorkExecutionDiagnosticsCoordinator? persistence)
    {
        this.previous = CurrentContext.Value;
        this.worker = worker;
        this.events = events;
        this.persistence = persistence;
        CurrentContext.Value = this;
    }

    public static WorkableLogCaptureContext? Current => CurrentContext.Value;

    public static IDisposable Begin(
        WorkerRecord worker,
        WorkerEventPublisher events,
        WorkExecutionDiagnosticsCoordinator? persistence = null)
        => new WorkableLogCaptureContext(worker, events, persistence);

    public bool IsEnabled(LogLevel logLevel)
        => this.worker.ShouldCaptureLog(logLevel) ||
            this.persistence?.IsLogEnabled(this.worker, logLevel) == true;

    public void Capture<TState>(
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var retainInMemory = this.worker.ShouldCaptureLog(logLevel);
        var persist = this.persistence?.IsLogEnabled(this.worker, logLevel) == true;
        if (!retainInMemory && !persist)
        {
            return;
        }

        var occurredAt = DateTimeOffset.UtcNow;
        WorkerLogEntry? entry = null;

        if (retainInMemory)
        {
            entry = this.worker.RecordLog(new WorkerLogEntry(
                occurredAt,
                this.worker.Id,
                this.worker.Work.Definition.Id,
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception?.GetType().FullName,
                exception?.Message));
            this.events.Log(this.worker, entry);
        }

        if (persist)
        {
            var activity = Activity.Current;
            this.persistence!.CaptureLog(
                this.worker,
                occurredAt,
                category,
                logLevel,
                eventId,
                entry,
                state,
                exception,
                formatter,
                activity is null ? null : activity.TraceId,
                activity is null ? null : activity.SpanId);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        CurrentContext.Value = this.previous;
    }
}
