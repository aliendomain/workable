using System.Threading;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class WorkableLogCaptureContext : IDisposable
{
    private static readonly AsyncLocal<WorkableLogCaptureContext?> CurrentContext = new();
    private readonly WorkableLogCaptureContext? previous;
    private readonly WorkerRecord worker;
    private readonly WorkerEventPublisher events;
    private bool disposed;

    private WorkableLogCaptureContext(WorkerRecord worker, WorkerEventPublisher events)
    {
        this.previous = CurrentContext.Value;
        this.worker = worker;
        this.events = events;
        CurrentContext.Value = this;
    }

    public static WorkableLogCaptureContext? Current => CurrentContext.Value;

    public static IDisposable Begin(WorkerRecord worker, WorkerEventPublisher events)
        => new WorkableLogCaptureContext(worker, events);

    public bool IsEnabled(LogLevel logLevel)
        => this.worker.ShouldCaptureLog(logLevel);

    public void Capture<TState>(
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        var entry = new WorkerLogEntry(
            DateTimeOffset.UtcNow,
            this.worker.Id,
            this.worker.Work.Definition.Id,
            category,
            logLevel,
            eventId,
            formatter(state, exception),
            exception?.GetType().FullName,
            exception?.Message,
            ExtractMetadata(state));

        this.worker.RecordLog(entry);
        this.events.Log(this.worker, entry);
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

    private static Dictionary<string, object?>? ExtractMetadata<TState>(TState state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values)
        {
            return null;
        }

        var metadata = new Dictionary<string, object?>();
        foreach (var value in values)
        {
            if (value.Key == "{OriginalFormat}")
            {
                continue;
            }

            metadata[value.Key] = value.Value;
        }

        return metadata.Count == 0 ? null : metadata;
    }
}
