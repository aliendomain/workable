namespace Workable;

public sealed class WorkableHttpQueryAdapter
{
    public Task<WorkComponentQueryResult> Components(
        IWorkSystem system,
        WorkComponentCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.Components(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkComponentQueryResult> View(
        IWorkSystem system,
        string name,
        WorkViewCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.View(name, criteria, cancellationToken: cancellationToken);
    }

    public async Task<WorkerSnapshot?> Worker(
        IWorkSystem system,
        WorkerId workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.Worker(workerId, cancellationToken: cancellationToken);
    }

    public async Task<WorkerIterationSnapshot?> WorkerIteration(
        IWorkSystem system,
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.WorkerIteration(iteration, cancellationToken: cancellationToken);
    }

    public Task<WorkerQueryResult> Workers(
        IWorkSystem system,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.Workers(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkerIterationQueryResult> WorkerIterations(
        IWorkSystem system,
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerIterations(criteria, cancellationToken: cancellationToken);
    }

    public async Task<WorkInfo?> WorkInfo(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.WorkInfo(definitionId, cancellationToken: cancellationToken);
    }

    public async Task<WorkInfo?> WorkInfo(
        IWorkSystem system,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return await system.Query.WorkInfo(name, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WorkDefinition>> WorkDefinitions(
        IWorkSystem system,
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return (await system.Query.WorkDefinitions(criteria, cancellationToken: cancellationToken)).Definitions;
    }

    public Task<WorkerKeyQueryResult> WorkerKeys(
        IWorkSystem system,
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerKeys(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        IWorkSystem system,
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerKeyTypes(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        IWorkSystem system,
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkIterationKeys(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        IWorkSystem system,
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkIterationKeyTypes(criteria, cancellationToken: cancellationToken);
    }

    public Task<WorkerStatusSummary> WorkerStatusSummary(
        IWorkSystem system,
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Query.WorkerStatusSummary(criteria, cancellationToken: cancellationToken);
    }
}
