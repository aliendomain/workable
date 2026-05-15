namespace Workable;

internal sealed record WorkerIterationQueryDefinition(WorkerIterationReference Iteration) :
    WorkQueryDefinition<WorkerIterationSnapshot>("workerIteration");
