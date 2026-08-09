using Microsoft.Extensions.Logging;

namespace Workable;

internal interface IWorkExecutionDiagnosticsSystem
{
    bool ExecutionDiagnosticsPersistenceAvailable { get; }

    Task<WorkExecutionDiagnosticQueryResult> QueryExecutionDiagnostics(
        WorkExecutionDiagnosticCriteria criteria,
        CancellationToken cancellationToken);

    Task<WorkExecutionDiagnosticArtifact?> GetExecutionDiagnostic(
        WorkExecutionDiagnosticGetRequest request,
        CancellationToken cancellationToken);

    IReadOnlyList<WorkExecutionDiagnosticCaptureRule> GetExecutionDiagnosticCaptureRules();

    Task<WorkExecutionDiagnosticCaptureRule> CreateExecutionDiagnosticCaptureRule(
        string? definitionName,
        LogLevel minimumLogLevel,
        WorkProfileCaptureMode? profileCaptureMode,
        TimeSpan activeFor,
        TimeSpan artifactRetention,
        WorkActor createdBy,
        CancellationToken cancellationToken);

    Task<bool> DeleteExecutionDiagnosticCaptureRule(Guid id, CancellationToken cancellationToken);
}
