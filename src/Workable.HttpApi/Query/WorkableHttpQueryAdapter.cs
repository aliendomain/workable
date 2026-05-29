namespace Workable;

public sealed class WorkableHttpQueryAdapter : WorkableViewQueryAdapter
{
    public async Task<WorkableHttpWorkerConfiguration?> WorkerConfiguration(
        IWorkSystemSession session,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        var worker = await this.Worker(session, workerId, cancellationToken);
        return worker is null
            ? null
            : new WorkableHttpWorkerConfiguration(
                worker.Options.ProfilingEnabled,
                WorkableHttpWorkConfiguration.From(worker.Configuration));
    }
}
