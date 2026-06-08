namespace Workable;

/// <summary>
/// Exposes buffering and delivery diagnostics for an event subscription.
/// </summary>
public interface IWorkEventSubscriptionDiagnostics
{
    /// <summary>
    /// Captures a point-in-time diagnostics snapshot for the subscription.
    /// </summary>
    /// <returns>The current subscription diagnostics snapshot.</returns>
    WorkEventSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot();
}
