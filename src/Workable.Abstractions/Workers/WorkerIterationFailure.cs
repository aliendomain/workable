using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Represents derived failure details for a retained worker iteration.
/// </summary>
/// <param name="Kind">Whether the failure was represented as a declared failure or an exception.</param>
/// <param name="Message">The most useful human-readable failure message Workable could derive.</param>
/// <param name="Code">The structured failure code, when one exists.</param>
/// <param name="Target">The structured failure target, when one exists.</param>
/// <param name="ExceptionType">The exception type, when the failure was exception-based.</param>
/// <param name="StackTrace">The retained stack trace, when one was captured.</param>
/// <param name="DeclaredByWork">Whether the failure was explicitly declared by work code instead of inferred from logs or exceptions.</param>
public sealed record WorkerIterationFailure(
    WorkerIterationFailureKind Kind,
    string Message,
    string? Code = null,
    string? Target = null,
    string? ExceptionType = null,
    string? StackTrace = null,
    bool DeclaredByWork = false);

/// <summary>
/// Identifies how Workable classified a retained iteration failure.
/// </summary>
public enum WorkerIterationFailureKind
{
    /// <summary>
    /// The iteration failed without a retained exception type.
    /// </summary>
    Failure,

    /// <summary>
    /// The iteration failed because of an exception.
    /// </summary>
    Exception,
}

/// <summary>
/// Derives a compact failure view from retained iteration messages and logs.
/// </summary>
public static class WorkerIterationFailureResolver
{
    /// <summary>
    /// Resolves derived failure details from a retained iteration snapshot.
    /// </summary>
    /// <param name="iteration">The iteration snapshot to inspect.</param>
    /// <returns>The derived failure details, or <see langword="null"/> when the iteration did not fail.</returns>
    public static WorkerIterationFailure? Resolve(WorkerIterationSnapshot iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        if (iteration.Status != WorkCompletionStatus.Failed)
        {
            return null;
        }

        return Resolve(iteration.Messages, iteration.Logs, "The retained iteration ended in failure.");
    }

    /// <summary>
    /// Resolves derived failure details from retained messages and logs.
    /// </summary>
    /// <param name="messages">The retained work messages to inspect.</param>
    /// <param name="logs">The retained worker log entries to inspect.</param>
    /// <param name="fallbackMessage">The fallback message to use when no better failure text can be derived.</param>
    /// <returns>The derived failure details.</returns>
    public static WorkerIterationFailure Resolve(
        IReadOnlyList<WorkMessage>? messages,
        IReadOnlyList<WorkerLogEntry>? logs,
        string fallbackMessage)
    {
        var errorMessage = messages?
            .FirstOrDefault(message => message.Severity.IsError());
        var metadata = errorMessage?.Metadata;
        var code = NormalizeText(errorMessage?.Code);
        var target = NormalizeText(errorMessage?.Target);
        var declaredByWork = string.Equals(
            ReadMetadataString(metadata, "failureSource"),
            "executionContext",
            StringComparison.OrdinalIgnoreCase);
        var exceptionType = NormalizeText(ReadMetadataString(metadata, "exceptionType"));
        var exceptionMessage = NormalizeText(
            ReadMetadataString(metadata, "exceptionMessage") ?? errorMessage?.Text);
        var stackTrace = NormalizeText(ReadMetadataString(metadata, "exceptionStackTrace"));
        if (!string.IsNullOrWhiteSpace(exceptionType))
        {
            return new WorkerIterationFailure(
                WorkerIterationFailureKind.Exception,
                string.IsNullOrWhiteSpace(exceptionMessage)
                    ? "The execution failed because an exception was raised."
                    : exceptionMessage,
                code,
                target,
                exceptionType,
                stackTrace,
                declaredByWork);
        }

        var latestFailureLog = logs?
            .OrderByDescending(entry => entry.OccurredAt)
            .FirstOrDefault(entry => entry.Level is LogLevel.Error or LogLevel.Critical);
        var fallbackExceptionType = NormalizeText(latestFailureLog?.ExceptionType);
        var fallbackExceptionMessage = NormalizeText(latestFailureLog?.ExceptionMessage);
        if (!string.IsNullOrWhiteSpace(fallbackExceptionType) || !string.IsNullOrWhiteSpace(fallbackExceptionMessage))
        {
            return new WorkerIterationFailure(
                string.IsNullOrWhiteSpace(fallbackExceptionType)
                    ? WorkerIterationFailureKind.Failure
                    : WorkerIterationFailureKind.Exception,
                fallbackExceptionMessage ?? fallbackMessage,
                code,
                target,
                fallbackExceptionType,
                null,
                declaredByWork);
        }

        return new WorkerIterationFailure(
            WorkerIterationFailureKind.Failure,
            NormalizeText(errorMessage?.Text) ?? fallbackMessage,
            code,
            target,
            null,
            null,
            declaredByWork);
    }

    private static string? ReadMetadataString(
        IReadOnlyDictionary<string, object?>? metadata,
        string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is string stringValue)
        {
            return stringValue;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        return value.ToString();
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
