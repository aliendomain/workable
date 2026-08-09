using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed record WorkExecutionDiagnosticsPolicy(
    TimeSpan Retention,
    LogLevel MinimumLogLevel,
    WorkProfileCaptureMode? ProfileCaptureMode,
    WorkExecutionDiagnosticCaptureSource CaptureSource);

internal sealed class WorkExecutionDiagnosticsPolicyResolver(
    WorkSystemExecutionDiagnosticsPersistenceConfiguration systemConfiguration)
{
    public WorkExecutionDiagnosticsPolicy? Resolve(WorkConfiguration configuration)
    {
        var work = configuration.ExecutionDiagnostics;
        var enabled = work.IsEnabled ?? systemConfiguration.IsEnabled;
        if (!enabled)
        {
            return null;
        }

        return work.IsEnabled == true
            ? new WorkExecutionDiagnosticsPolicy(
                work.Retention,
                work.MinimumLogLevel,
                work.ProfileCaptureMode,
                WorkExecutionDiagnosticCaptureSource.WorkConfiguration)
            : new WorkExecutionDiagnosticsPolicy(
                systemConfiguration.Retention,
                systemConfiguration.MinimumLogLevel,
                systemConfiguration.ProfileCaptureMode,
                WorkExecutionDiagnosticCaptureSource.SystemConfiguration);
    }
}
