using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Workable;
internal sealed class WorkableLogger<T>(IServiceProvider services) : ILogger<T>
{
    private static readonly string Category = typeof(T).FullName ?? typeof(T).Name;
    private readonly ILogger inner = services.GetService<ILoggerFactory>()?.CreateLogger(Category) ?? NullLogger.Instance;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => this.inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => this.inner.IsEnabled(logLevel) ||
            WorkableLogCaptureContext.Current?.IsEnabled(logLevel) == true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        this.inner.Log(logLevel, eventId, state, exception, formatter);
        WorkableLogCaptureContext.Current?.Capture(Category, logLevel, eventId, state, exception, formatter);
    }
}
