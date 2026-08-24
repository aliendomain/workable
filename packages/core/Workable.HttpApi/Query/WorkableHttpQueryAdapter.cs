namespace Workable;

/// <summary>
/// Adapts Workable query and view data into the HTTP API's specialized response shapes.
/// </summary>
public sealed class WorkableHttpQueryAdapter : WorkableViewQueryAdapter
{
    /// <summary>
    /// Initializes an HTTP query adapter without an exception logger.
    /// </summary>
    public WorkableHttpQueryAdapter()
    {
    }

    /// <summary>
    /// Initializes an HTTP query adapter with logging for unexpected component failures.
    /// </summary>
    /// <param name="logger">The logger that receives unexpected component failures.</param>
    public WorkableHttpQueryAdapter(Microsoft.Extensions.Logging.ILogger<WorkableViewQueryAdapter> logger)
        : base(logger)
    {
    }

    /// <summary>
    /// Builds the HTTP work-info payload for a visible definition.
    /// </summary>
    /// <param name="session">The authorized session used to read the definition and worker rollup.</param>
    /// <param name="system">The system whose queue-request schema should be advertised.</param>
    /// <param name="name">The definition name to resolve.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The HTTP work-info payload, or <see langword="null"/> when the definition is not visible to the caller.</returns>
    public async Task<WorkableHttpWorkInfo?> DefinitionInfo(
        IWorkSystemSession session,
        IWorkSystem system,
        string name,
        CancellationToken cancellationToken = default)
    {
        var info = await this.WorkInfo(session, name, cancellationToken);
        return info is null
            ? null
            : new WorkableHttpWorkInfo(
                info.Definition,
                info.Status,
                info.Workers,
                WorkableHttpQueueRequestDescriptor.Create(system));
    }

    /// <summary>
    /// Builds the worker-configuration payload used by HTTP configuration and queue-editor screens.
    /// </summary>
    /// <param name="session">The authorized session used to read the worker and definition data.</param>
    /// <param name="system">The system whose queue-request schema should be advertised.</param>
    /// <param name="workerId">The worker identifier to resolve.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The worker-configuration payload, or <see langword="null"/> when the worker is not visible to the caller.</returns>
    public async Task<WorkableHttpWorkerConfiguration?> WorkerConfiguration(
        IWorkSystemSession session,
        IWorkSystem system,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        var worker = await this.Worker(session, workerId, cancellationToken);
        return worker is null
            ? null
            : new WorkableHttpWorkerConfiguration(
                worker.Options.ProfilingEnabled,
                WorkableHttpWorkConfiguration.From(worker.Configuration),
                worker.Input,
                worker.SubjectId,
                worker.ConcurrencyKey,
                await this.WorkInfo(session, worker.DefinitionName, cancellationToken),
                WorkableHttpQueueRequestDescriptor.Create(system))
            {
                ProfilingCaptureMode = worker.Options.ProfilingCaptureMode,
            };
    }

}
