namespace Workable;

/// <summary>
/// Exposes queueing and coalescing diagnostics for a change subscription.
/// </summary>
public interface IWorkChangeSubscriptionDiagnostics
{
    /// <summary>
    /// Captures a point-in-time diagnostics snapshot for the subscription.
    /// </summary>
    /// <returns>The current subscription diagnostics snapshot.</returns>
    WorkChangeSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot();
}
