using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient.Diagnostics;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerCommandProfilingObserver :
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>,
    IDisposable
{
    private const string InstrumentationName = WorkProfileInstrumentation.SqlClient;
    private const int MaximumStatementLength = 8_192;
    private const int MaximumParameterTextLength = 1_024;
    private const int MaximumCapturedParameters = 32;
    private const int MaximumParameterContextLength = 4_096;
    private const int MaximumExceptionMessageLength = 1_024;
    private const int MaximumMetadataLength = 512;

    private readonly IWorkProfilingContextAccessor profilingContextAccessor;
    private readonly ConcurrentDictionary<WorkSystemId, byte> activeSystems = new();
    private readonly ConcurrentDictionary<Guid, ActiveSqlCommand> activeCommands = new();
    private readonly ConcurrentDictionary<WorkSystemId, ConcurrentDictionary<Guid, ActiveSqlCommand>> activeCommandsBySystem = new();
    private readonly ConcurrentDictionary<DiagnosticListener, IDisposable> listenerSubscriptions = new();
    private readonly IDisposable allListenersSubscription;
    private int activeCommandCount;
    private int disposed;

    public WorkableSqlServerCommandProfilingObserver(
        IWorkProfilingContextAccessor profilingContextAccessor)
    {
        this.profilingContextAccessor = profilingContextAccessor;
        this.allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal WorkableSqlServerCommandProfilingObserver(
        WorkSystemId systemId,
        IWorkProfilingContextAccessor profilingContextAccessor)
        : this(profilingContextAccessor)
        => this.RegisterSystem(systemId);

    internal void RegisterSystem(WorkSystemId systemId)
    {
        this.activeCommandsBySystem.GetOrAdd(systemId, static _ => new());
        this.activeSystems.TryAdd(systemId, 0);
    }

    internal void UnregisterSystem(WorkSystemId systemId)
    {
        this.activeSystems.TryRemove(systemId, out _);
        if (!this.activeCommandsBySystem.TryRemove(systemId, out var systemCommands))
        {
            return;
        }

        foreach (var entry in systemCommands)
        {
            if (systemCommands.TryRemove(entry.Key, out _) &&
                this.TryRemoveActiveCommand(entry.Key, out var active))
            {
                active.FinalizeIncomplete();
            }
        }
    }

    public void OnNext(DiagnosticListener value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (this.IsDisposed || !IsSqlClientListener(value) || this.listenerSubscriptions.ContainsKey(value))
        {
            return;
        }

        var subscription = value.Subscribe(this, this.IsRelevantEvent);
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

        this.activeSystems.Clear();
        this.activeCommandsBySystem.Clear();
        foreach (var entry in this.activeCommands)
        {
            if (this.TryRemoveActiveCommand(entry.Key, out var active))
            {
                active.FinalizeIncomplete();
            }
        }
    }

    private void HandleBefore(SqlClientCommandBefore before)
    {
        if (!this.TryGetCurrentProfilingContext(out var profilingContext))
        {
            return;
        }

        var pendingRegistry = profilingContext.Profiler as IWorkProfilePendingInstrumentationRegistry;
        if (pendingRegistry is not null && !pendingRegistry.TryEnterPendingInstrumentationRegistration())
        {
            return;
        }

        try
        {
            if (!profilingContext.TryStartAutomaticTiming(
                InstrumentationName,
                CreateLabel(before.Operation),
                () => CreateContext(before.Operation, before.Command),
                out SqlCommandProfileContext? context,
                out var scope))
            {
                return;
            }

            var active = new ActiveSqlCommand(
                this,
                before.OperationId,
                profilingContext.SystemId,
                context!,
                scope!,
                pendingRegistry);
            while (!this.TryAddActiveCommand(before.OperationId, active))
            {
                if (this.TryRemoveActiveCommand(before.OperationId, out var existing))
                {
                    existing.FinalizeIncomplete();
                }

                if (this.IsDisposed)
                {
                    active.FinalizeIncomplete();
                    return;
                }
            }

            var systemCommands = this.activeCommandsBySystem.GetOrAdd(
                profilingContext.SystemId,
                static _ => new());
            systemCommands.TryAdd(before.OperationId, active);
            pendingRegistry?.RegisterPendingInstrumentation(active);
            active.RemovePendingRegistrationIfCompleted();
            if ((this.IsDisposed || !this.activeSystems.ContainsKey(profilingContext.SystemId)) &&
                this.TryRemoveActiveCommand(before.OperationId, out var added))
            {
                systemCommands.TryRemove(before.OperationId, out _);
                added.FinalizeIncomplete();
            }
        }
        finally
        {
            pendingRegistry?.ExitPendingInstrumentationRegistration();
        }
    }

    private void HandleAfter(SqlClientCommandAfter after)
    {
        if (this.TryRemoveActiveCommand(after.OperationId, out var active))
        {
            this.RemoveFromSystemIndex(after.OperationId, active);
            active.Complete();
        }
    }

    private void HandleError(SqlClientCommandError error)
    {
        if (this.TryRemoveActiveCommand(error.OperationId, out var active))
        {
            this.RemoveFromSystemIndex(error.OperationId, active);
            active.Fail(error.Exception);
            return;
        }

        if (this.TryGetCurrentProfilingContext(out var profilingContext))
        {
            profilingContext.TryAddAutomaticInfo(
                InstrumentationName,
                "SQL Error",
                () => CreateFailureContext(error.Operation, error.Command, error.Exception));
        }
    }

    private bool IsDisposed => Volatile.Read(ref this.disposed) != 0;

    private bool TryGetCurrentProfilingContext(out WorkProfilingContext profilingContext)
    {
        if (this.IsDisposed ||
            !this.profilingContextAccessor.TryGetCurrent(out profilingContext) ||
            !this.activeSystems.ContainsKey(profilingContext.SystemId))
        {
            profilingContext = default;
            return false;
        }

        return true;
    }

    private void FinalizeForProfileSnapshot(Guid operationId, ActiveSqlCommand expected)
    {
        if (this.TryRemoveActiveCommand(operationId, out var active))
        {
            this.RemoveFromSystemIndex(operationId, active);
            active.FinalizeIncomplete();
            return;
        }

        expected.WaitForCompletion();
    }

    private void RemoveFromSystemIndex(Guid operationId, ActiveSqlCommand active)
    {
        if (this.activeCommandsBySystem.TryGetValue(active.SystemId, out var systemCommands))
        {
            systemCommands.TryRemove(operationId, out _);
        }
    }

    private static string CreateLabel(string? operation)
        => $"SQL {NormalizeOperation(operation)}";

    private static bool IsSqlClientListener(DiagnosticListener listener)
        => listener.Name.Contains("SqlClient", StringComparison.OrdinalIgnoreCase);

    private bool IsRelevantEvent(string eventName)
    {
        if (string.Equals(eventName, SqlClientCommandBefore.Name, StringComparison.Ordinal))
        {
            return this.TryGetCurrentProfilingContext(out _);
        }

        if (!string.Equals(eventName, SqlClientCommandAfter.Name, StringComparison.Ordinal) &&
            !string.Equals(eventName, SqlClientCommandError.Name, StringComparison.Ordinal))
        {
            return false;
        }

        return Volatile.Read(ref this.activeCommandCount) > 0 ||
            this.TryGetCurrentProfilingContext(out _);
    }

    private bool TryAddActiveCommand(Guid operationId, ActiveSqlCommand active)
    {
        if (!this.activeCommands.TryAdd(operationId, active))
        {
            return false;
        }

        Interlocked.Increment(ref this.activeCommandCount);
        return true;
    }

    private bool TryRemoveActiveCommand(Guid operationId, out ActiveSqlCommand active)
    {
        if (!this.activeCommands.TryRemove(operationId, out active!))
        {
            return false;
        }

        Interlocked.Decrement(ref this.activeCommandCount);
        return true;
    }

    private static SqlCommandProfileContext CreateContext(
        string? operation,
        Microsoft.Data.SqlClient.SqlCommand command)
    {
        var parameters = CreateParameters(command);
        var statement = CaptureStatement(command.CommandText);
        return new SqlCommandProfileContext(
            Provider: "Microsoft.Data.SqlClient",
            Operation: NormalizeOperation(operation),
            CommandType: command.CommandType.ToString(),
            StatementKind: InferStatementKind(command.CommandText),
            Statement: statement.Value,
            StatementTruncated: statement.IsTruncated,
            ParameterCount: command.Parameters.Count,
            CapturedParameterCount: parameters.Values.Count,
            ParametersTruncated: parameters.IsTruncated,
            Parameters: parameters.Values,
            Database: TruncateNullable(command.Connection?.Database, MaximumMetadataLength),
            HasTransaction: command.Transaction is not null);
    }

    private static SqlCommandFailureContext CreateFailureContext(
        string? operation,
        Microsoft.Data.SqlClient.SqlCommand command,
        Exception exception)
    {
        var parameters = CreateParameters(command);
        var statement = CaptureStatement(command.CommandText);
        var message = Truncate(exception.Message, MaximumExceptionMessageLength, out var messageTruncated);
        return new SqlCommandFailureContext(
            Provider: "Microsoft.Data.SqlClient",
            Operation: NormalizeOperation(operation),
            CommandType: command.CommandType.ToString(),
            StatementKind: InferStatementKind(command.CommandText),
            Statement: statement.Value,
            StatementTruncated: statement.IsTruncated,
            ParameterCount: command.Parameters.Count,
            CapturedParameterCount: parameters.Values.Count,
            ParametersTruncated: parameters.IsTruncated,
            Parameters: parameters.Values,
            Database: TruncateNullable(command.Connection?.Database, MaximumMetadataLength),
            ExceptionType: Truncate(
                exception.GetType().FullName ?? exception.GetType().Name,
                MaximumMetadataLength,
                out _),
            Message: message,
            MessageTruncated: messageTruncated);
    }

    private static string NormalizeOperation(string? operation)
    {
        if (string.IsNullOrEmpty(operation))
        {
            return "Command";
        }

        var bounded = operation.AsSpan(0, Math.Min(operation.Length, MaximumMetadataLength)).Trim();
        if (bounded.IsEmpty)
        {
            return "Command";
        }

        return bounded.Length == operation.Length ? operation : bounded.ToString();
    }

    private static CapturedText CaptureStatement(string? commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return new CapturedText("<empty>", false);
        }

        var capturedLength = Math.Min(commandText.Length, MaximumStatementLength);
        var captured = commandText.AsSpan(0, capturedLength);
        if (captured.Trim().IsEmpty)
        {
            return new CapturedText("<empty>", commandText.Length > capturedLength);
        }

        return new CapturedText(
            capturedLength == commandText.Length ? commandText : captured.ToString(),
            commandText.Length > capturedLength);
    }

    private static string InferStatementKind(string? commandText)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return "UNKNOWN";
        }

        var remaining = commandText
            .AsSpan(0, Math.Min(commandText.Length, MaximumStatementLength))
            .TrimStart();
        if (remaining.IsEmpty)
        {
            return "UNKNOWN";
        }

        var separatorIndex = remaining.IndexOfAny(" \t\r\n");
        var tokenLength = separatorIndex < 0 ? remaining.Length : separatorIndex;
        tokenLength = Math.Min(tokenLength, MaximumMetadataLength);
        var token = remaining[..tokenLength];
        while (!token.IsEmpty && token[^1] is ';' or ',')
        {
            token = token[..^1];
        }

        return token.ToString().ToUpperInvariant();
    }

    private static CapturedParameters CreateParameters(Microsoft.Data.SqlClient.SqlCommand command)
    {
        var parameters = new List<SqlCommandParameterContext>(
            Math.Min(command.Parameters.Count, MaximumCapturedParameters));
        var remainingLength = MaximumParameterContextLength;
        foreach (Microsoft.Data.SqlClient.SqlParameter parameter in command.Parameters)
        {
            if (parameters.Count >= MaximumCapturedParameters || remainingLength <= 0)
            {
                break;
            }

            var captured = CreateParameter(parameter, remainingLength);
            parameters.Add(captured);
            remainingLength -= EstimateParameterContextLength(captured);
        }

        return new CapturedParameters(parameters, parameters.Count < command.Parameters.Count);
    }

    private static SqlCommandParameterContext CreateParameter(
        Microsoft.Data.SqlClient.SqlParameter parameter,
        int maximumContextLength)
    {
        var originalName = string.IsNullOrWhiteSpace(parameter.ParameterName)
            ? "<unnamed>"
            : parameter.ParameterName;
        var isRedacted = ShouldRedactParameter(originalName);
        var isBinaryOmitted = !isRedacted && IsBinaryParameter(parameter);
        var value = isRedacted
            ? new CapturedParameterValue("<redacted>", false)
            : isBinaryOmitted
                ? new CapturedParameterValue("<binary omitted>", false)
                : CaptureParameterValue(parameter.Value, Math.Min(MaximumParameterTextLength, maximumContextLength));

        // SqlParameter enforces a 128-character name limit, below Workable's metadata cap.
        return new SqlCommandParameterContext(
            Name: originalName,
            Value: value.Value,
            Type: parameter.SqlDbType.ToString(),
            Direction: parameter.Direction.ToString(),
            IsRedacted: isRedacted,
            IsBinaryOmitted: isBinaryOmitted,
            IsTruncated: value.IsTruncated);
    }

    private static int EstimateParameterContextLength(SqlCommandParameterContext parameter)
        => parameter.Name.Length +
            parameter.Type.Length +
            parameter.Direction.Length +
            ((parameter.Value as string)?.Length ?? 0) +
            32;

    private static bool ShouldRedactParameter(string parameterName)
    {
        if (parameterName.Length > MaximumMetadataLength)
        {
            return true;
        }

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
        var builder = new StringBuilder(Math.Min(parameterName.Length, MaximumMetadataLength));
        foreach (var character in parameterName)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool IsBinaryParameter(Microsoft.Data.SqlClient.SqlParameter parameter)
        => parameter.SqlDbType is System.Data.SqlDbType.Binary or
            System.Data.SqlDbType.Image or
            System.Data.SqlDbType.Timestamp or
            System.Data.SqlDbType.VarBinary ||
            parameter.Value is byte[];

    private static CapturedParameterValue CaptureParameterValue(object? value, int maximumTextLength)
        => value switch
        {
            null or DBNull => new(null, false),
            string text => CaptureText(text, maximumTextLength),
            char character => new(character.ToString(), false),
            char[] characters => CaptureText(
                new string(characters, 0, Math.Min(characters.Length, maximumTextLength)),
                maximumTextLength,
                characters.Length > maximumTextLength),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => new(value, false),
            Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan => new(value, false),
            Enum enumValue => new(enumValue.ToString(), false),
            byte[] => new("<binary omitted>", false),
            IEnumerable when value is not string => new($"<{value.GetType().Name}>", false),
            _ => new($"<{value.GetType().Name}>", false)
        };

    private static CapturedParameterValue CaptureText(
        string value,
        int maximumLength,
        bool alreadyTruncated = false)
    {
        var captured = Truncate(value, Math.Max(0, maximumLength), out var truncated);
        return new(captured, alreadyTruncated || truncated);
    }

    private static string Truncate(string value, int maximumLength, out bool truncated)
    {
        truncated = value.Length > maximumLength;
        return truncated ? value[..maximumLength] : value;
    }

    private static string? TruncateNullable(string? value, int maximumLength)
        => value is null ? null : Truncate(value, maximumLength, out _);

    internal static object CaptureCommandForBenchmark(Microsoft.Data.SqlClient.SqlCommand command)
        => CreateContext("ExecuteReader", command);

    internal static object CaptureFailedCommandForBenchmark(
        Microsoft.Data.SqlClient.SqlCommand command,
        Exception exception)
    {
        var timing = CreateContext("ExecuteReader", command);
        timing.Fail(exception);
        return timing;
    }

    private sealed class ActiveSqlCommand(
        WorkableSqlServerCommandProfilingObserver owner,
        Guid operationId,
        WorkSystemId systemId,
        SqlCommandProfileContext context,
        IWorkProfileScope scope,
        IWorkProfilePendingInstrumentationRegistry? pendingRegistry) : IWorkProfilePendingInstrumentation
    {
        private int completionState;

        public WorkSystemId SystemId { get; } = systemId;

        public void Complete() => this.Finish(context.Complete);

        public void Fail(Exception exception) => this.Finish(() => context.Fail(exception));

        public void FinalizeIncomplete() => this.Finish(context.FinalizeIncomplete);

        public void FinalizeForProfileSnapshot()
            => owner.FinalizeForProfileSnapshot(operationId, this);

        public void RemovePendingRegistrationIfCompleted()
        {
            if (Volatile.Read(ref this.completionState) == 2)
            {
                pendingRegistry?.UnregisterPendingInstrumentation(this);
            }
        }

        public void WaitForCompletion()
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref this.completionState) == 1)
            {
                spinner.SpinOnce();
            }
        }

        private void Finish(Action completeContext)
        {
            if (Interlocked.CompareExchange(ref this.completionState, 1, 0) != 0)
            {
                this.WaitForCompletion();
                return;
            }

            try
            {
                using (scope)
                {
                    completeContext();
                }
            }
            finally
            {
                Volatile.Write(ref this.completionState, 2);
                pendingRegistry?.UnregisterPendingInstrumentation(this);
            }
        }
    }

    private sealed record SqlCommandProfileContext(
        string Provider,
        string Operation,
        string CommandType,
        string StatementKind,
        string Statement,
        bool StatementTruncated,
        int ParameterCount,
        int CapturedParameterCount,
        bool ParametersTruncated,
        IReadOnlyList<SqlCommandParameterContext> Parameters,
        string? Database,
        bool HasTransaction)
    {
        public string Outcome { get; private set; } = "Pending";

        public string? ExceptionType { get; private set; }

        public string? Message { get; private set; }

        public bool MessageTruncated { get; private set; }

        public void Complete() => this.Outcome = "Completed";

        public void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            this.ExceptionType = Truncate(
                exception.GetType().FullName ?? exception.GetType().Name,
                MaximumMetadataLength,
                out _);
            this.Message = Truncate(exception.Message, MaximumExceptionMessageLength, out var messageTruncated);
            this.MessageTruncated = messageTruncated;
            this.Outcome = "Faulted";
        }

        public void FinalizeIncomplete() => this.Outcome = "Incomplete";
    }

    private sealed record SqlCommandFailureContext(
        string Provider,
        string Operation,
        string CommandType,
        string StatementKind,
        string Statement,
        bool StatementTruncated,
        int ParameterCount,
        int CapturedParameterCount,
        bool ParametersTruncated,
        IReadOnlyList<SqlCommandParameterContext> Parameters,
        string? Database,
        string ExceptionType,
        string Message,
        bool MessageTruncated);

    private sealed record SqlCommandParameterContext(
        string Name,
        object? Value,
        string Type,
        string Direction,
        bool IsRedacted,
        bool IsBinaryOmitted,
        bool IsTruncated);

    private readonly record struct CapturedParameterValue(object? Value, bool IsTruncated);

    private readonly record struct CapturedText(string Value, bool IsTruncated);

    private readonly record struct CapturedParameters(
        IReadOnlyList<SqlCommandParameterContext> Values,
        bool IsTruncated);

}
