namespace Workable;

internal sealed partial class WorkQueryService
{
    private sealed class WorkSystemQueryContext
    {
        private readonly WorkQueryService owner;
        private readonly WorkSystemCriteria? criteria;
        private readonly Lazy<HashSet<WorkDefinitionId>?> definitionIds;
        private readonly Lazy<int> definitionCount;
        private readonly Lazy<WorkSystemWorkerCounts> workerCounts;
        private readonly Lazy<WorkSystemIterationCounts> iterationCounts;
        private readonly Lazy<SystemCatalogLevel> catalogLevel;
        private readonly Lazy<IReadOnlyList<WorkIterationKeyTypeFacet>> commonKeyTypes;
        private readonly Lazy<IReadOnlyList<WorkerOverviewItem>> failedWorkers;
        private readonly Lazy<IReadOnlyList<WorkerIterationOverviewItem>> failedIterations;
        private readonly Lazy<IReadOnlyList<WorkerIterationOverviewItem>> completedIterations;

        public WorkSystemQueryContext(WorkQueryService owner, WorkSystemCriteria? criteria)
        {
            this.owner = owner;
            this.criteria = criteria;
            this.definitionIds = new Lazy<HashSet<WorkDefinitionId>?>(() => this.owner.ResolveDefinitionScope(this.criteria));
            this.definitionCount = new Lazy<int>(() => this.owner.index.ActiveOrQueuedDefinitionCount(this.DefinitionIds));
            this.workerCounts = new Lazy<WorkSystemWorkerCounts>(() => this.owner.CreateSystemWorkerCounts(this.DefinitionIds));
            this.iterationCounts = new Lazy<WorkSystemIterationCounts>(() => this.owner.CreateSystemIterationCounts(this.DefinitionIds));
            this.catalogLevel = new Lazy<SystemCatalogLevel>(() => this.owner.CreateSystemCatalogLevel(this.criteria));
            this.commonKeyTypes = new Lazy<IReadOnlyList<WorkIterationKeyTypeFacet>>(() => this.owner.CreateSystemCommonKeyTypes(this.DefinitionIds));
            this.failedWorkers = new Lazy<IReadOnlyList<WorkerOverviewItem>>(() => this.owner.CreateSystemFailedWorkers(this.DefinitionIds));
            this.failedIterations = new Lazy<IReadOnlyList<WorkerIterationOverviewItem>>(() => this.owner.CreateSystemFailedIterations(this.DefinitionIds));
            this.completedIterations = new Lazy<IReadOnlyList<WorkerIterationOverviewItem>>(() => this.owner.CreateSystemCompletedIterations(this.DefinitionIds));
        }

        public WorkSystemWorkerCounts WorkerCounts => this.workerCounts.Value;

        public WorkSystemIterationCounts IterationCounts => this.iterationCounts.Value;

        public IReadOnlyList<WorkIterationKeyTypeFacet> CommonKeyTypes => this.commonKeyTypes.Value;

        public IReadOnlyList<WorkerOverviewItem> FailedWorkers => this.failedWorkers.Value;

        public IReadOnlyList<WorkerIterationOverviewItem> FailedIterations => this.failedIterations.Value;

        public IReadOnlyList<WorkerIterationOverviewItem> CompletedIterations => this.completedIterations.Value;

        private HashSet<WorkDefinitionId>? DefinitionIds => this.definitionIds.Value;

        public WorkSystemDetails CreateDetails()
            => new(
                this.owner.workSystemName,
                this.owner.getSystemState(),
                this.definitionCount.Value,
                this.catalogLevel.Value.Categories,
                this.catalogLevel.Value.Definitions,
                this.WorkerCounts.ActiveWorkerCount,
                this.WorkerCounts.FinalWorkerCount,
                this.WorkerCounts.FailedWorkerCount,
                this.WorkerCounts.WorkerCountByState,
                this.IterationCounts.CurrentIterationCount,
                this.IterationCounts.CompletedIterationCount,
                this.IterationCounts.FailedIterationCount,
                this.IterationCounts.CanceledIterationCount,
                this.IterationCounts.IterationCountByStatus,
                this.CommonKeyTypes,
                this.criteria?.IncludeThroughput == true ? this.CreateThroughput() : null,
                this.FailedWorkers,
                this.FailedIterations,
                this.CompletedIterations);

        public WorkSystemThroughput CreateThroughput(WorkThroughputCriteria? throughput = null)
            => this.owner.CreateSystemThroughput(this.DefinitionIds, throughput);

        public WorkSystemFailedWorkers CreateFailedWorkers()
            => new(
                this.WorkerCounts.ActiveWorkerCount,
                this.WorkerCounts.FinalWorkerCount,
                this.WorkerCounts.FailedWorkerCount,
                this.WorkerCounts.WorkerCountByState,
                this.FailedWorkers);
    }
}
