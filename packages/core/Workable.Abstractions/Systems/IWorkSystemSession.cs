namespace Workable;

/// <summary>
/// Exposes caller-scoped access to a Workable system.
/// </summary>
/// <remarks>
/// Sessions capture a <see cref="WorkRequestContext"/> at creation time. Workable evaluates authorization for the
/// session surfaces using that caller context instead of exposing the system as globally open.
/// </remarks>
public interface IWorkSystemSession
{
    /// <summary>
    /// Gets the name of the system the session is scoped to, or <see langword="null"/> for the default unnamed system.
    /// </summary>
    string? SystemName { get; }

    /// <summary>
    /// Gets the current lifecycle state of the underlying system.
    /// </summary>
    WorkSystemState SystemState { get; }

    /// <summary>
    /// Gets the optional capability snapshot for the underlying system.
    /// </summary>
    WorkSystemCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the caller-scoped diagnostics surface.
    /// </summary>
    IWorkSystemDiagnostics Diagnostics { get; }

    /// <summary>
    /// Gets the caller-scoped catalog surface.
    /// </summary>
    IWorkCatalog Catalog { get; }

    /// <summary>
    /// Gets the caller-scoped queue surface.
    /// </summary>
    IWorkQueueService Queue { get; }

    /// <summary>
    /// Gets the caller-scoped worker-operations surface.
    /// </summary>
    IWorkerOperations Workers { get; }

    /// <summary>
    /// Gets the caller-scoped query surface.
    /// </summary>
    IWorkQueryService Query { get; }

    /// <summary>
    /// Gets the caller-scoped event-stream surface.
    /// </summary>
    IWorkEventStream Events { get; }

    /// <summary>
    /// Gets the caller-scoped change-stream surface.
    /// </summary>
    IWorkChangeStream Changes { get; }
}
