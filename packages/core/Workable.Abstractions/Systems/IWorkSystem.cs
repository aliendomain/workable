using System.Diagnostics.CodeAnalysis;

namespace Workable;
/// <summary>
/// Represents one hosted Workable system and exposes its direct runtime surfaces.
/// </summary>
/// <remarks>
/// Direct access to several members requires authorization to be disabled for the system. When authorization is enabled,
/// callers should create a session with <see cref="CreateSession(WorkRequestContext)"/> so access can be evaluated
/// against the supplied request context.
/// </remarks>
public interface IWorkSystem : IAsyncDisposable
{
    /// <summary>
    /// Gets the stable identifier for this system instance.
    /// </summary>
    WorkSystemId Id { get; }

    /// <summary>
    /// Gets the configured system name, or <see langword="null"/> for the default unnamed system.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Gets a value indicating whether Workable enforces authorization on this system.
    /// </summary>
    bool RequiresAuthorization { get; }

    /// <summary>
    /// Gets the current lifecycle state of the system.
    /// </summary>
    WorkSystemState State { get; }

    /// <summary>
    /// Gets the direct catalog surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkCatalog Catalog { get; }

    /// <summary>
    /// Gets the direct queue surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkQueueService Queue { get; }

    /// <summary>
    /// Gets the direct worker-operations surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkerOperations Workers { get; }

    /// <summary>
    /// Gets the direct query surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkQueryService Query { get; }

    /// <summary>
    /// Gets the direct event-stream surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkEventStream Events { get; }

    /// <summary>
    /// Gets the direct change-stream surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkChangeStream Changes { get; }

    /// <summary>
    /// Gets the direct diagnostics surface for the system.
    /// </summary>
    /// <remarks>
    /// This member is intended for open systems. When authorization is required, access may throw because no request
    /// context is available for evaluation.
    /// </remarks>
    IWorkSystemDiagnostics Diagnostics { get; }

    /// <summary>
    /// Describes the caller's effective system-level access for the supplied request context.
    /// </summary>
    /// <param name="requestContext">The caller context to evaluate against the system authorization rules.</param>
    /// <returns>A summary of the caller's effective system and work-wide access.</returns>
    WorkSystemAccessSummary DescribeAccess(WorkRequestContext requestContext);

    /// <summary>
    /// Creates a caller-scoped session over the system surfaces.
    /// </summary>
    /// <param name="requestContext">
    /// The caller context whose identity, origin, and authorization snapshot should govern the returned session.
    /// </param>
    /// <returns>A session whose catalog, queue, query, worker, event, and diagnostics access is scoped to the caller.</returns>
    IWorkSystemSession CreateSession(WorkRequestContext requestContext);

    /// <summary>
    /// Starts the system using a caller-scoped request context.
    /// </summary>
    /// <param name="requestContext">The caller context recorded for the start action and used for access checks.</param>
    /// <param name="cancellationToken">A token that cancels the start request before startup completes.</param>
    /// <returns>A task that completes when startup finishes or fails.</returns>
    Task Start(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the system using a caller-scoped request context.
    /// </summary>
    /// <param name="requestContext">The caller context recorded for the stop action and used for access checks.</param>
    /// <param name="cancellationToken">A token that cancels the stop request before shutdown completes.</param>
    /// <returns>A task that completes with the shutdown result once the stop operation finishes.</returns>
    Task<WorkSystemStopResult> Stop(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);
}
