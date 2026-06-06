namespace Workable;

public sealed class WorkableHttpQueryAdapter : WorkableViewQueryAdapter
{
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
                WorkableHttpQueueRequestDescriptor.Create(system));
    }

    public async Task<WorkableHttpWorkerIterationDetail?> WorkerIterationDetail(
        IWorkSystemSession session,
        WorkerId workerId,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        var worker = await this.Worker(session, workerId, cancellationToken);
        if (worker is null)
        {
            return null;
        }

        var iteration = await this.WorkerIteration(
            session,
            new WorkerIterationReference(workerId, sequence),
            cancellationToken);
        if (iteration is null)
        {
            return null;
        }

        var messages = await this.WorkerIterationMessages(
            session,
            new WorkerIterationReference(workerId, sequence),
            new WorkIterationMessageCriteria(Take: 1),
            cancellationToken);
        var logs = await this.WorkerIterationLogs(
            session,
            new WorkerIterationReference(workerId, sequence),
            new WorkIterationLogCriteria(Take: 50),
            cancellationToken);

        return new WorkableHttpWorkerIterationDetail(
            worker.Id,
            worker.DefinitionName,
            worker.SubjectId,
            worker.ConcurrencyKey,
            worker.Identifiers,
            worker.Input,
            new WorkableHttpWorkerIterationSnapshot(
                iteration.Sequence,
                iteration.StartedAt,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                iteration.OccurredAt,
                iteration.Status,
                iteration.AttemptCount,
                iteration.IsFinal,
                iteration.Output,
                iteration.Failure,
                iteration.Profile),
            messages?.Summary ?? new WorkIterationMessageSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
            logs ?? new WorkIterationLogSection(
                new WorkWorkerOverviewLogSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
                new WorkWorkerOverviewPage<WorkerLogEntry>([], false, null)));
    }
}
