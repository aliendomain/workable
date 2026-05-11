using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkLoggingConfiguration
{
    public static WorkLoggingConfiguration Default { get; } = new();

    public bool IsEnabled { get; init; } = true;

    public LogLevel Level { get; init; } = LogLevel.Information;

    public int MaximumBufferedEntries { get; init; } = 100;
}
