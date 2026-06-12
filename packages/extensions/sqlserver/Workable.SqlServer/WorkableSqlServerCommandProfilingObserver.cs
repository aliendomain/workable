using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient.Diagnostics;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerCommandProfilingObserver :
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>,
    IDisposable
{
    private readonly WorkSystemId systemId;
    private readonly IWorkProfilingContextAccessor profilingContextAccessor;
    private readonly ConcurrentDictionary<Guid, IWorkProfileScope> activeCommands = new();
    private readonly ConcurrentDictionary<DiagnosticListener, IDisposable> listenerSubscriptions = new();
    private readonly IDisposable allListenersSubscription;
    private int disposed;

    public WorkableSqlServerCommandProfilingObserver(
        WorkSystemId systemId,
        IWorkProfilingContextAccessor profilingContextAccessor)
    {
        this.systemId = systemId;
        this.profilingContextAccessor = profilingContextAccessor;
        this.allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        this.SubscribeToExistingListeners();
    }

    public void OnNext(DiagnosticListener value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (this.IsDisposed || !IsSqlClientListener(value))
        {
            return;
        }

        if (this.listenerSubscriptions.ContainsKey(value))
        {
            return;
        }

        var subscription = value.Subscribe(this, IsRelevantEvent);
        if (!this.listenerSubscriptions.TryAdd(value, subscription))
        {
            subscription.Dispose();
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (this.IsDisposed)
        {
            return;
        }

        switch (value.Value)
        {
            case SqlClientCommandBefore before:
                this.HandleBefore(before);
                break;
            case SqlClientCommandAfter after:
                this.HandleAfter(after);
                break;
            case SqlClientCommandError error:
                this.HandleError(error);
                break;
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.allListenersSubscription.Dispose();
        foreach (var listener in this.listenerSubscriptions.Keys.ToArray())
        {
            if (this.listenerSubscriptions.TryRemove(listener, out var subscription))
            {
                subscription.Dispose();
            }
        }

        foreach (var operationId in this.activeCommands.Keys.ToArray())
        {
            if (this.activeCommands.TryRemove(operationId, out var scope))
            {
                scope.Dispose();
            }
        }
    }

    private void SubscribeToExistingListeners()
    {
        try
        {
            var method = typeof(DiagnosticListener).GetMethod(
                "GetAllListeners",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method?.Invoke(null, null) is IEnumerable<DiagnosticListener> listeners)
            {
                foreach (var listener in listeners)
                {
                    this.OnNext(listener);
                }

                return;
            }
        }
        catch (Exception exception) when (IsReflectionAccessException(exception))
        {
            // Best effort only. The live AllListeners subscription still captures future listeners.
        }

        try
        {
            var listenersField = typeof(DiagnosticListener).GetField(
                "s_allListeners",
                BindingFlags.Static | BindingFlags.NonPublic);
            var listenersLockField = typeof(DiagnosticListener).GetField(
                "s_allListenersLock",
                BindingFlags.Static | BindingFlags.NonPublic);
            var listenersLock = listenersLockField?.GetValue(null);
            if (listenersLock is null)
            {
                SubscribeToExistingListeners(ReadListeners(listenersField?.GetValue(null)));
                return;
            }

            lock (listenersLock)
            {
                SubscribeToExistingListeners(ReadListeners(listenersField?.GetValue(null)));
            }
        }
        catch (Exception exception) when (IsReflectionAccessException(exception))
        {
            // Best effort only. The live AllListeners subscription still captures future listeners.
        }
    }

    private static IReadOnlyList<DiagnosticListener> ReadListeners(object? source)
        => source switch
        {
            null => [],
            IEnumerable<DiagnosticListener> typed => [.. typed],
            IEnumerable untyped => [.. untyped.OfType<DiagnosticListener>()],
            _ => [],
        };

    private static bool IsReflectionAccessException(Exception exception)
        => exception is AmbiguousMatchException or
            TargetException or
            TargetInvocationException or
            TargetParameterCountException or
            MemberAccessException or
            NotSupportedException or
            TypeLoadException or
            InvalidOperationException;

    private void SubscribeToExistingListeners(IEnumerable<DiagnosticListener> listeners)
    {
        foreach (var listener in listeners)
        {
            this.OnNext(listener);
        }
    }

    private void HandleBefore(SqlClientCommandBefore before)
    {
        if (!this.TryGetCurrentProfiler(out var profiler))
        {
            return;
        }

        var scope = profiler.StartTiming(
            CreateLabel(before.Operation),
            CreateContext(before.Operation, before.Command));
        while (!this.activeCommands.TryAdd(before.OperationId, scope))
        {
            if (this.activeCommands.TryRemove(before.OperationId, out var existing))
            {
                existing.Dispose();
            }

            if (this.IsDisposed)
            {
                scope.Dispose();
                return;
            }
        }

        if (this.IsDisposed && this.activeCommands.TryRemove(before.OperationId, out var activeScope))
        {
            activeScope.Dispose();
        }
    }

    private void HandleAfter(SqlClientCommandAfter after)
    {
        if (this.activeCommands.TryRemove(after.OperationId, out var scope))
        {
            scope.Dispose();
        }
    }

    private void HandleError(SqlClientCommandError error)
    {
        if (this.activeCommands.TryRemove(error.OperationId, out var scope))
        {
            scope.Dispose();
        }

        if (this.TryGetCurrentProfiler(out var profiler))
        {
            profiler.AddInfo(
                "SQL Error",
                CreateFailureContext(error.Operation, error.Command, error.Exception));
        }
    }

    private bool IsDisposed => Volatile.Read(ref this.disposed) != 0;

    private bool TryGetCurrentProfiler([NotNullWhen(true)] out IWorkProfiler? profiler)
    {
        if (this.IsDisposed ||
            !this.profilingContextAccessor.TryGetCurrent(out var context) ||
            context.SystemId != this.systemId)
        {
            profiler = null;
            return false;
        }

        profiler = context.Profiler;
        return true;
    }

    private static string CreateLabel(string? operation)
        => string.IsNullOrWhiteSpace(operation)
            ? "SQL Command"
            : $"SQL {operation.Trim()}";

    private static bool IsSqlClientListener(DiagnosticListener listener)
        => listener.Name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelevantEvent(string eventName)
        => string.Equals(eventName, SqlClientCommandBefore.Name, StringComparison.Ordinal) ||
            string.Equals(eventName, SqlClientCommandAfter.Name, StringComparison.Ordinal) ||
            string.Equals(eventName, SqlClientCommandError.Name, StringComparison.Ordinal);

    private static SqlCommandProfileContext CreateContext(string? operation, Microsoft.Data.SqlClient.SqlCommand command)
        => new(
            Provider: "Microsoft.Data.SqlClient",
            Operation: NormalizeOperation(operation),
            CommandType: command.CommandType.ToString(),
            StatementKind: InferStatementKind(command.CommandText),
            Statement: CaptureStatement(command.CommandText),
            ParameterCount: command.Parameters.Count,
            Parameters: CreateParameters(command),
            Database: command.Connection?.Database,
            HasTransaction: command.Transaction is not null);

    private static SqlCommandFailureContext CreateFailureContext(
        string? operation,
        Microsoft.Data.SqlClient.SqlCommand command,
        Exception exception)
        => new(
            Provider: "Microsoft.Data.SqlClient",
            Operation: NormalizeOperation(operation),
            CommandType: command.CommandType.ToString(),
            StatementKind: InferStatementKind(command.CommandText),
            Statement: CaptureStatement(command.CommandText),
            ParameterCount: command.Parameters.Count,
            Parameters: CreateParameters(command),
            Database: command.Connection?.Database,
            ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
            Message: exception.Message);

    private static string NormalizeOperation(string? operation)
        => string.IsNullOrWhiteSpace(operation) ? "Command" : operation.Trim();

    private static string InferStatementKind(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return "UNKNOWN";
        }

        var trimmedStart = commandText.TrimStart();
        if (trimmedStart.Length == 0)
        {
            return "UNKNOWN";
        }

        var separatorIndex = trimmedStart.IndexOfAny([' ', '\t', '\r', '\n']);
        var token = separatorIndex < 0 ? trimmedStart : trimmedStart[..separatorIndex];
        return token.TrimEnd(';', ',').ToUpperInvariant();
    }

    private static string CaptureStatement(string? commandText)
        => string.IsNullOrWhiteSpace(commandText) ? "<empty>" : commandText;

    private static IReadOnlyList<SqlCommandParameterContext> CreateParameters(Microsoft.Data.SqlClient.SqlCommand command)
        => [.. command.Parameters
            .Cast<Microsoft.Data.SqlClient.SqlParameter>()
            .Select(CreateParameter)];

    private static SqlCommandParameterContext CreateParameter(Microsoft.Data.SqlClient.SqlParameter parameter)
    {
        var name = string.IsNullOrWhiteSpace(parameter.ParameterName)
            ? "<unnamed>"
            : parameter.ParameterName;
        var isRedacted = ShouldRedactParameter(name);

        return new(
            Name: name,
            Value: isRedacted ? "<redacted>" : CaptureParameterValue(parameter.Value),
            Type: parameter.SqlDbType.ToString(),
            Direction: parameter.Direction.ToString(),
            IsRedacted: isRedacted);
    }

    private static bool ShouldRedactParameter(string parameterName)
    {
        var normalized = NormalizeParameterName(parameterName);
        return normalized.Contains("password", StringComparison.Ordinal) ||
            normalized == "pwd" ||
            normalized.Contains("secret", StringComparison.Ordinal) ||
            normalized.Contains("accesstoken", StringComparison.Ordinal) ||
            normalized.Contains("refreshtoken", StringComparison.Ordinal) ||
            normalized.EndsWith("token", StringComparison.Ordinal) ||
            normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("accesskey", StringComparison.Ordinal) ||
            normalized.Contains("privatekey", StringComparison.Ordinal) ||
            normalized.Contains("sharedaccesssignature", StringComparison.Ordinal);
    }

    private static string NormalizeParameterName(string parameterName)
    {
        var builder = new StringBuilder(parameterName.Length);
        foreach (var character in parameterName)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static object? CaptureParameterValue(object? value)
        => value switch
        {
            null or DBNull => null,
            string text => text,
            char character => character.ToString(),
            char[] characters => new string(characters),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => value,
            Enum enumValue => enumValue.ToString(),
            byte[] bytes => CaptureBinary(bytes),
            IEnumerable when value is not string => $"<{value.GetType().Name}>",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? $"<{value.GetType().Name}>",
        };

    private static string CaptureBinary(byte[] bytes)
        => $"0x{Convert.ToHexString(bytes)}";

    private sealed record SqlCommandProfileContext(
        string Provider,
        string Operation,
        string CommandType,
        string StatementKind,
        string Statement,
        int ParameterCount,
        IReadOnlyList<SqlCommandParameterContext> Parameters,
        string? Database,
        bool HasTransaction);

    private sealed record SqlCommandFailureContext(
        string Provider,
        string Operation,
        string CommandType,
        string StatementKind,
        string Statement,
        int ParameterCount,
        IReadOnlyList<SqlCommandParameterContext> Parameters,
        string? Database,
        string ExceptionType,
        string Message);

    private sealed record SqlCommandParameterContext(
        string Name,
        object? Value,
        string Type,
        string Direction,
        bool IsRedacted);
}
