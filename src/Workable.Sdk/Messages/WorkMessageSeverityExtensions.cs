namespace Workable;

public static class WorkMessageSeverityExtensions
{
    public static bool IsError(this WorkMessageSeverity severity)
        => severity is WorkMessageSeverity.Error or WorkMessageSeverity.Critical;
}
