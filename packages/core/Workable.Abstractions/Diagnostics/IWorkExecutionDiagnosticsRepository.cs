using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Persists and queries logs and profiles captured for work iterations.
/// </summary>
public interface IWorkExecutionDiagnosticsRepository
{
    /// <summary>
    /// Initializes repository storage for a work system.
    /// </summary>
    Task Initialize(
        WorkExecutionDiagnosticsInitializationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins one iteration artifact before log entries are appended.
    /// </summary>
    Task BeginIteration(
        WorkExecutionDiagnosticIterationStart iteration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends captured logs in occurrence order.
    /// </summary>
    Task AppendLogs(
        IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizes an iteration artifact with its outcome and profile.
    /// </summary>
    Task CompleteIteration(
        WorkExecutionDiagnosticIterationCompletion completion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a bounded batch of expired and abandoned artifacts.
    /// </summary>
    Task<int> DeleteExpired(
        WorkExecutionDiagnosticsExpirationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries completed, unexpired iteration artifacts.
    /// </summary>
    Task<WorkExecutionDiagnosticQueryResult> Query(
        WorkExecutionDiagnosticCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one completed, unexpired iteration artifact.
    /// </summary>
    Task<WorkExecutionDiagnosticArtifact?> Get(
        WorkExecutionDiagnosticGetRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists unexpired temporary capture rules for a work system.
    /// </summary>
    Task<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>> ListCaptureRules(
        WorkExecutionDiagnosticsInitializationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a temporary capture rule.
    /// </summary>
    Task UpsertCaptureRule(
        WorkExecutionDiagnosticCaptureRule rule,
        int maximumActiveRules,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a temporary capture rule.
    /// </summary>
    Task<bool> DeleteCaptureRule(
        WorkExecutionDiagnosticCaptureRuleDeleteRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes repository initialization for one Workable system.
/// </summary>
public sealed record WorkExecutionDiagnosticsInitializationContext(
    WorkSystemId WorkSystemId,
    string? WorkSystemName);

/// <summary>
/// Identifies why an iteration was persisted.
/// </summary>
public enum WorkExecutionDiagnosticCaptureSource
{
    SystemConfiguration,
    WorkConfiguration,
    TemporarySystemRule,
    TemporaryWorkRule,
}

/// <summary>
/// Begins one persisted iteration artifact.
/// </summary>
public sealed record WorkExecutionDiagnosticIterationStart(
    Guid DiagnosticId,
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    long IterationSequence,
    WorkDefinitionId DefinitionId,
    string DefinitionName,
    DateTimeOffset StartedAt,
    TimeSpan Retention,
    LogLevel MinimumLogLevel,
    WorkProfileCaptureMode? ProfileCaptureMode,
    WorkExecutionDiagnosticInstrumentationAvailability InstrumentationAvailability,
    WorkExecutionDiagnosticCaptureSource CaptureSource);

/// <summary>
/// Records which automatic dependency profilers were available when an iteration executed.
/// </summary>
public sealed record WorkExecutionDiagnosticInstrumentationAvailability(
    bool SqlClientProfilingAvailable,
    bool HttpClientProfilingAvailable);

/// <summary>
/// Stores one persistent worker log independently of the retained snapshot buffer.
/// </summary>
public sealed record WorkExecutionDiagnosticLogRecord(
    Guid DiagnosticId,
    long Ordinal,
    DateTimeOffset OccurredAt,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? PropertiesJson,
    string? ExceptionType,
    string? ExceptionMessage,
    string? ExceptionStackTrace,
    string? TraceId,
    string? SpanId);

/// <summary>
/// Finalizes one persisted iteration artifact.
/// </summary>
public sealed record WorkExecutionDiagnosticIterationCompletion(
    Guid DiagnosticId,
    WorkCompletionStatus Status,
    int AttemptCount,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkProfileSnapshot? Profile,
    bool ProfileDropped,
    long PersistedLogCount,
    long DroppedLogCount,
    IReadOnlyList<WorkExecutionInstrumentationSummary> Instrumentation);

/// <summary>
/// Summarizes profile nodes from one stable instrumentation source.
/// </summary>
public sealed record WorkExecutionInstrumentationSummary(
    string Instrumentation,
    int NodeCount,
    int TimingCount,
    long TotalTimingMilliseconds,
    long MaximumTimingMilliseconds,
    int OmittedNodeCount = 0);

/// <summary>
/// Requests physical expiration cleanup.
/// </summary>
public sealed record WorkExecutionDiagnosticsExpirationRequest(
    WorkSystemId WorkSystemId,
    DateTimeOffset ExpiresBefore,
    DateTimeOffset AbandonedBefore,
    int MaximumCount = 1_000)
{
    /// <summary>
    /// Gets incomplete diagnostics known to still be executing in this process.
    /// </summary>
    public IReadOnlySet<Guid> ActiveDiagnosticIds { get; init; } = new HashSet<Guid>();
}

/// <summary>
/// Selects persisted iteration diagnostics.
/// </summary>
public sealed record WorkExecutionDiagnosticCriteria(
    WorkSystemId WorkSystemId,
    string? DefinitionName = null,
    WorkerId? WorkerId = null,
    DateTimeOffset? CompletedAfter = null,
    DateTimeOffset? CompletedBefore = null,
    LogLevel? MinimumLogLevel = null,
    int Take = 100);

/// <summary>
/// Selects one persisted iteration artifact.
/// </summary>
public sealed record WorkExecutionDiagnosticGetRequest(
    WorkSystemId WorkSystemId,
    WorkerId WorkerId,
    long IterationSequence,
    int MaximumLogCount = 10_000);

/// <summary>
/// Returns a page of persisted iteration summaries.
/// </summary>
public sealed record WorkExecutionDiagnosticQueryResult(
    IReadOnlyList<WorkExecutionDiagnosticSummary> Items);

/// <summary>
/// Describes one completed persisted iteration without its full log and profile payload.
/// </summary>
public sealed record WorkExecutionDiagnosticSummary(
    Guid DiagnosticId,
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    long IterationSequence,
    WorkDefinitionId DefinitionId,
    string DefinitionName,
    WorkCompletionStatus Status,
    int AttemptCount,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkExecutionDiagnosticCaptureSource CaptureSource,
    WorkProfileCaptureMode? ProfileCaptureMode,
    WorkExecutionDiagnosticInstrumentationAvailability InstrumentationAvailability,
    bool ProfileDropped,
    long PersistedLogCount,
    long DroppedLogCount,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<WorkExecutionInstrumentationSummary> Instrumentation);

/// <summary>
/// Returns the complete persisted evidence for one iteration.
/// </summary>
public sealed record WorkExecutionDiagnosticArtifact(
    WorkExecutionDiagnosticSummary Summary,
    IReadOnlyList<WorkExecutionDiagnosticLogRecord> Logs,
    WorkProfileSnapshot? Profile)
{
    /// <summary>
    /// Gets whether additional persisted logs were omitted from this response.
    /// </summary>
    public bool LogsTruncated { get; init; }
}

/// <summary>
/// Temporarily persists future iteration logs and optionally enables profiling.
/// </summary>
public sealed record WorkExecutionDiagnosticCaptureRule(
    Guid Id,
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    string? DefinitionName,
    LogLevel MinimumLogLevel,
    WorkProfileCaptureMode? ProfileCaptureMode,
    TimeSpan ArtifactRetention,
    DateTimeOffset CreatedAt,
    DateTimeOffset ActiveUntil,
    WorkActor CreatedBy);

/// <summary>
/// Selects a temporary capture rule to delete.
/// </summary>
public sealed record WorkExecutionDiagnosticCaptureRuleDeleteRequest(
    WorkSystemId WorkSystemId,
    Guid RuleId);
