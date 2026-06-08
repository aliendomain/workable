namespace Workable;

/// <summary>
/// Provides helper methods for working with <see cref="WorkMessageSeverity"/>.
/// </summary>
public static class WorkMessageSeverityExtensions
{
    /// <summary>
    /// Determines whether a message severity represents an error condition.
    /// </summary>
    /// <param name="severity">The severity to inspect.</param>
    /// <returns><see langword="true"/> when the severity is <see cref="WorkMessageSeverity.Error"/> or <see cref="WorkMessageSeverity.Critical"/>; otherwise <see langword="false"/>.</returns>
    public static bool IsError(this WorkMessageSeverity severity)
        => severity is WorkMessageSeverity.Error or WorkMessageSeverity.Critical;
}
