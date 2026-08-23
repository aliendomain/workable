namespace Workable;

internal sealed class WorkIterationStatusCursorOutOfRangeException : ArgumentOutOfRangeException
{
    public WorkIterationStatusCursorOutOfRangeException(string parameterName, string message)
        : base(parameterName, message)
    {
    }

    public WorkIterationStatusCursorOutOfRangeException(
        string parameterName,
        object? actualValue,
        string message)
        : base(parameterName, actualValue, message)
    {
    }
}
