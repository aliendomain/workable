using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Creates a temporary persistent execution-diagnostics rule.
/// </summary>
public sealed record WorkableHttpCreateExecutionDiagnosticCaptureRuleRequest(
    string? DefinitionName = null,
    LogLevel MinimumLogLevel = LogLevel.Information,
    WorkProfileCaptureMode? ProfileCaptureMode = null,
    int ActiveForMinutes = 30,
    int ArtifactRetentionMinutes = 1_440,
    string? Description = null);

/// <summary>
/// Describes persistent execution-diagnostics availability and active temporary rules.
/// </summary>
public sealed record WorkableHttpExecutionDiagnosticCaptureState(
    bool PersistenceAvailable,
    IReadOnlyList<WorkExecutionDiagnosticCaptureRule> Rules);
