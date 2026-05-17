using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Workable.SampleHost;

internal sealed class WorkableSampleConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "workable-sample";
    private const string ResetColor = "\x1b[0m";

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrWhiteSpace(message) && logEntry.Exception is null)
        {
            return;
        }

        textWriter.Write(FormatLogLevel(logEntry.LogLevel));
        if (ShouldIncludeCategory(logEntry.LogLevel) && !string.IsNullOrWhiteSpace(logEntry.Category))
        {
            textWriter.Write(" [");
            textWriter.Write(logEntry.Category);
            textWriter.Write(']');
        }

        textWriter.Write(": ");
        if (!string.IsNullOrWhiteSpace(message))
        {
            textWriter.WriteLine(message);
        }
        else
        {
            textWriter.WriteLine();
        }

        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine(logEntry.Exception);
        }
    }

    private static string FormatLogLevel(LogLevel level)
        => $"{FormatLogLevelColor(level)}{FormatLogLevelText(level)}{ResetColor}";

    private static string FormatLogLevelText(LogLevel level)
        => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none"
        };

    private static bool ShouldIncludeCategory(LogLevel level)
        => level >= LogLevel.Warning;

    private static string FormatLogLevelColor(LogLevel level)
        => level switch
        {
            LogLevel.Trace => "\x1b[37m",
            LogLevel.Debug => "\x1b[37m",
            LogLevel.Information => "\x1b[32m",
            LogLevel.Warning => "\x1b[33m",
            LogLevel.Error => "\x1b[31m",
            LogLevel.Critical => "\x1b[35m",
            _ => "\x1b[37m"
        };
}
