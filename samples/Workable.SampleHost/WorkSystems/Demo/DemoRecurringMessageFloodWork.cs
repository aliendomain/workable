using Workable;

namespace SampleHost.Demo;

public sealed record DemoRecurringMessageFloodInput();

public sealed record DemoRecurringMessageFloodOutput(
    int MessageCount,
    int LogEntryCount,
    DateTimeOffset CompletedAt);

public sealed class DemoRecurringMessageFloodWork(
    ILogger<DemoRecurringMessageFloodWork> logger) : IWorkExecutor<DemoRecurringMessageFloodInput, DemoRecurringMessageFloodOutput>
{
    private const int LogEntryCount = 198;
    private const int CriticalLogCount = 33;
    private const int ErrorLogCount = 33;
    private const int WarningLogCount = 33;
    private const int InformationLogCount = 33;
    private const int DebugLogCount = 33;
    private const int TraceLogCount = 33;

    public Task<WorkExecutionResult<DemoRecurringMessageFloodOutput>> Execute(
        IWorkExecutionContext context,
        DemoRecurringMessageFloodInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteLogs(logger);

        return Task.FromResult(WorkExecutionResult<DemoRecurringMessageFloodOutput>.Success(
            new DemoRecurringMessageFloodOutput(
                0,
                LogEntryCount,
                DateTimeOffset.UtcNow)));
    }

    private static void WriteLogs(ILogger logger)
    {
        for (var index = 1; index <= CriticalLogCount; index++)
        {
            logger.LogCritical(
                "Recurring message flood critical log {EntryIndex} of {EntryCount}.",
                index,
                LogEntryCount);
        }

        for (var index = 1; index <= ErrorLogCount; index++)
        {
            logger.LogError(
                "Recurring message flood error log {EntryIndex} of {EntryCount}.",
                index,
                LogEntryCount);
        }

        for (var index = 1; index <= WarningLogCount; index++)
        {
            logger.LogWarning(
                "Recurring message flood warning log {EntryIndex} of {EntryCount}.",
                index,
                LogEntryCount);
        }

        for (var index = 1; index <= InformationLogCount; index++)
        {
            logger.LogInformation(
                "Recurring message flood info log {EntryIndex} of {EntryCount}.",
                index,
                LogEntryCount);
        }

        for (var index = 1; index <= DebugLogCount; index++)
        {
            logger.LogDebug(
                "Recurring message flood debug log {EntryIndex} of {EntryCount}.",
                index,
                LogEntryCount);
        }

        for (var index = 1; index <= TraceLogCount; index++)
        {
            logger.LogTrace(
                "Recurring message flood trace log {EntryIndex} of {EntryCount}.",
                index,
                LogEntryCount);
        }
    }
}
