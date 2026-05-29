using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Workable;

public sealed record WorkerIterationFailure(
    WorkerIterationFailureKind Kind,
    string Message,
    string? Code = null,
    string? Target = null,
    string? ExceptionType = null,
    string? StackTrace = null,
    bool DeclaredByWork = false);

public enum WorkerIterationFailureKind
{
    Failure,
    Exception,
}

public static class WorkerIterationFailureResolver
{
    public static WorkerIterationFailure? Resolve(WorkerIterationSnapshot iteration)
    {
        ArgumentNullException.ThrowIfNull(iteration);
        if (iteration.Status != WorkCompletionStatus.Failed)
        {
            return null;
        }

        return Resolve(iteration.Messages, iteration.Logs, "The retained iteration ended in failure.");
    }

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
