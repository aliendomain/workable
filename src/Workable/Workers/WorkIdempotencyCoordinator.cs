using System.Collections.Concurrent;

namespace Workable;

internal sealed class WorkIdempotencyCoordinator(
    WorkerIndex index,
    ConcurrentDictionary<WorkerId, WorkerRecord> workers,
    WorkSystemIdempotencyDiagnosticsTracker diagnostics)
{
    public IReadOnlyList<WorkMessage> Validate(
        WorkDefinitionId definitionId,
        WorkSubjectId? subjectId,
        WorkIdempotencyConfiguration idempotency,
        bool includeActiveWorkerConflicts = true)
    {
        if (!idempotency.IsEnabled)
        {
            return [];
        }

        if (subjectId is not { } requiredSubjectId)
        {
            return [WorkMessage.Error(
                "workable.idempotency.subject_required",
                "Idempotent work requires a work subject id.",
                "input.subjectId")];
        }

        if (!includeActiveWorkerConflicts)
        {
            return [];
        }

        var conflicts = this.GetSubjectWorkers(definitionId, requiredSubjectId)
            .Where(worker => worker.State is not WorkerState.Canceled and not WorkerState.Interrupted)
            .ToList();

        if (conflicts.Count == 0)
        {
            return [];
        }

        diagnostics.RecordDuplicateRejected(definitionId, requiredSubjectId, idempotency.Storage);
        return [WorkMessage.Error(
                "workable.idempotency.duplicate_subject",
                $"A worker already exists for work subject '{requiredSubjectId}'.",
                "input.subjectId")];
    }

    public IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(WorkSubjectId subjectId)
        => this.GetSnapshotsNewestFirst(index.BySubject(subjectId));

    public IReadOnlyList<WorkerSnapshot> GetSubjectWorkers(
        WorkDefinitionId definitionId,
        WorkSubjectId subjectId)
        => this.GetSnapshotsNewestFirst(index.ByDefinitionAndSubject(definitionId, subjectId));

    private IReadOnlyList<WorkerSnapshot> GetSnapshotsNewestFirst(IEnumerable<WorkerId> workerIds)
        => [.. workerIds
            .Select(workerId => workers.TryGetValue(workerId, out var worker) ? worker.ToSnapshot() : null)
            .OfType<WorkerSnapshot>()
            .OrderByDescending(worker => worker.CreatedAt)];
}
