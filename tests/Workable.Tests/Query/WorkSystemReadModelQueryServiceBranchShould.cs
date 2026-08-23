using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Query")]
public sealed class WorkSystemReadModelQueryServiceBranchShould
{
    [Fact]
    public void DefinitionMatchingCoversNameCategorySearchAndScopeOperands()
    {
        var definition = WorkDefinition.Create(
            "billing.close",
            "Closes an invoice.",
            "Billing:Invoices");

        Assert.True(Matches(definition, new WorkDefinitionCriteria()));
        Assert.True(Matches(definition, new WorkDefinitionCriteria(Names: new HashSet<string> { "BILLING.CLOSE" })));
        Assert.False(Matches(definition, new WorkDefinitionCriteria(Names: new HashSet<string> { "other" })));
        Assert.True(Matches(definition, new WorkDefinitionCriteria(Name: "BILLING.CLOSE")));
        Assert.False(Matches(definition, new WorkDefinitionCriteria(Name: "other")));
        Assert.True(Matches(definition, new WorkDefinitionCriteria(Category: "billing", IncludeSubcategories: true)));
        Assert.False(Matches(definition, new WorkDefinitionCriteria(Category: "shipping", IncludeSubcategories: true)));
        Assert.False(Matches(definition, new WorkDefinitionCriteria(Category: "billing", IncludeSubcategories: false)));
        Assert.True(Matches(definition, new WorkDefinitionCriteria(Search: "close")));
        Assert.True(Matches(definition, new WorkDefinitionCriteria(Search: "invoice")));
        Assert.False(Matches(definition, new WorkDefinitionCriteria(Search: "missing")));
        Assert.False(Matches(
            WorkDefinition.Create("billing.empty", description: null, category: "Billing"),
            new WorkDefinitionCriteria(Search: "missing")));

        Assert.True(Matches(definition, new WorkSystemCriteria()));
        Assert.True(Matches(definition, new WorkSystemCriteria(DefinitionNames: new HashSet<string> { "billing.close" })));
        Assert.False(Matches(definition, new WorkSystemCriteria(DefinitionNames: new HashSet<string> { "other" })));
        Assert.True(Matches(definition, new WorkSystemCriteria(DefinitionName: "BILLING.CLOSE")));
        Assert.False(Matches(definition, new WorkSystemCriteria(DefinitionName: "other")));
        Assert.True(Matches(definition, new WorkSystemCriteria(Category: "Billing", IncludeSubcategories: true)));
        Assert.False(Matches(definition, new WorkSystemCriteria(Category: "Billing", IncludeSubcategories: false)));
    }

    [Fact]
    public void WholeSystemSummaryDetectionRejectsEveryFilterDimension()
    {
        Assert.True(InvokeStatic<bool>("IsWholeSystemStatusSummary", new WorkerCriteria()));
        var filtered = new WorkerCriteria[]
        {
            new(DefinitionNames: new HashSet<string> { "work" }),
            new(DefinitionName: "work"),
            new(Category: "category"),
            new(SubjectId: new WorkSubjectId("type", "value")),
            new(ConcurrencyKey: new WorkConcurrencyKey("type", "value")),
            new(Identifier: new WorkIdentifier("type", "value")),
            new(States: new HashSet<WorkerState> { WorkerState.Waiting }),
            new(Configuration: new WorkerConfigurationCriteria(RecurrenceEnabled: true)),
            new(CreatedFrom: DateTimeOffset.UtcNow),
            new(CreatedTo: DateTimeOffset.UtcNow),
            new(UpdatedFrom: DateTimeOffset.UtcNow),
            new(UpdatedTo: DateTimeOffset.UtcNow),
        };
        Assert.All(filtered, criteria =>
            Assert.False(InvokeStatic<bool>("IsWholeSystemStatusSummary", criteria)));
    }

    [Fact]
    public void WorkerSortingCoversEverySortAndDirection()
    {
        var now = DateTimeOffset.UtcNow;
        var alpha = new WorkerOverviewItem(
            WorkerId.New(),
            "alpha",
            null,
            null,
            new HashSet<WorkIdentifier>(),
            1,
            "Category",
            WorkerState.Waiting,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-2),
            now.AddMinutes(-1));
        var beta = alpha with
        {
            Id = WorkerId.New(),
            DefinitionName = "beta",
            State = WorkerState.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var values = new[] { beta, alpha };

        foreach (var sort in new[]
                 {
                     WorkerCriteriaSort.CreatedAt,
                     WorkerCriteriaSort.UpdatedAt,
                     WorkerCriteriaSort.DefinitionName,
                     WorkerCriteriaSort.State,
                 })
        {
            var ascending = SortWorkers(values, sort, WorkCriteriaSortDirection.Ascending).ToArray();
            var descending = SortWorkers(values, sort, WorkCriteriaSortDirection.Descending).ToArray();
            Assert.Equal(2, ascending.Length);
            Assert.Equal(ascending.Reverse().Select(item => item.Id), descending.Select(item => item.Id));
        }
    }

    [Fact]
    public void IterationSortingCoversEverySortAndDirection()
    {
        var now = DateTimeOffset.UtcNow;
        var alpha = new WorkerIterationOverviewItem(
            WorkerId.New(),
            1,
            "alpha",
            "Category",
            WorkerState.Waiting,
            WorkCompletionStatus.Completed,
            now.AddMinutes(-3),
            now.AddMinutes(-2),
            TimeSpan.FromSeconds(1),
            null,
            null,
            []);
        var beta = alpha with
        {
            WorkerId = WorkerId.New(),
            Sequence = 2,
            DefinitionName = "beta",
            Status = WorkCompletionStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            ExecutionDuration = TimeSpan.FromSeconds(2),
        };
        var values = new[] { beta, alpha };

        foreach (var sort in new[]
                 {
                     WorkerIterationCriteriaSort.CompletedAt,
                     WorkerIterationCriteriaSort.StartedAt,
                     WorkerIterationCriteriaSort.ExecutionDuration,
                     WorkerIterationCriteriaSort.DefinitionName,
                     WorkerIterationCriteriaSort.Status,
                 })
        {
            var ascending = SortIterations(values, sort, WorkCriteriaSortDirection.Ascending).ToArray();
            var descending = SortIterations(values, sort, WorkCriteriaSortDirection.Descending).ToArray();
            Assert.Equal(2, ascending.Length);
            Assert.Equal(
                ascending.Reverse().Select(item => item.Sequence),
                descending.Select(item => item.Sequence));
        }
    }

    [Fact]
    public void SearchNormalizationCoversIgnoredTypeValueAndTakeBoundaries()
    {
        Assert.True(InvokeStatic<bool>("MatchesWorkKeySearch", "invoice", "123", null, true));
        Assert.True(InvokeStatic<bool>("MatchesWorkKeySearch", "invoice", "123", "all workers for the key", true));
        Assert.True(InvokeStatic<bool>("MatchesWorkKeySearch", "invoice", "123", "invoice", false));
        Assert.True(InvokeStatic<bool>("MatchesWorkKeySearch", "invoice", "123", "123", true));
        Assert.False(InvokeStatic<bool>("MatchesWorkKeySearch", "invoice", "123", "123", false));
        Assert.False(InvokeStatic<bool>("MatchesWorkKeySearch", "invoice", "123", "invoice missing", true));

        foreach (var ignored in new[] { "all", "for", "id", "key", "keys", "the", "work", "worker", "workers" })
        {
            Assert.True(InvokeStatic<bool>("IsIgnoredWorkKeySearchTerm", ignored));
        }
        Assert.False(InvokeStatic<bool>("IsIgnoredWorkKeySearchTerm", "invoice"));

        Assert.Null(InvokeStatic<string?>("NormalizeActorId", (object?)null));
        Assert.Null(InvokeStatic<string?>("NormalizeActorId", " "));
        Assert.Equal("actor", InvokeStatic<string?>("NormalizeActorId", " actor "));
        Assert.Equal(WorkerCriteria.DefaultTake, InvokeStatic<int>("NormalizeWorkerTake", 0));
        Assert.Equal(WorkerCriteria.MaximumTake, InvokeStatic<int>("NormalizeWorkerTake", int.MaxValue));
        Assert.Equal(5, InvokeStatic<int>("NormalizeWorkerTake", 5));
        Assert.Equal(WorkerIterationCriteria.DefaultTake, InvokeStatic<int>("NormalizeWorkerIterationTake", -1));
        Assert.Equal(WorkerIterationCriteria.MaximumTake, InvokeStatic<int>("NormalizeWorkerIterationTake", int.MaxValue));
        Assert.Equal(WorkerKeyCriteria.DefaultTake, InvokeStatic<int>("NormalizeWorkKeyTake", 0));
        Assert.Equal(WorkerKeyCriteria.MaximumTake, InvokeStatic<int>("NormalizeWorkKeyTake", int.MaxValue));
        Assert.Equal(WorkIterationKeyCriteria.DefaultTake, InvokeStatic<int>("NormalizeWorkIterationKeyTake", 0));
        Assert.Equal(WorkIterationKeyCriteria.MaximumTake, InvokeStatic<int>("NormalizeWorkIterationKeyTake", int.MaxValue));
    }

    [Fact]
    public void WorkerMatchingRejectsEveryIndependentFilterDimension()
    {
        var now = DateTimeOffset.UtcNow;
        var subject = new WorkSubjectId("invoice", "1");
        var concurrency = new WorkConcurrencyKey("tenant", "2");
        var identifier = new WorkIdentifier("order", "3");
        var worker = new WorkerReadModelWorker(
            WorkDefinitionId.New(),
            new WorkerOverviewItem(
                WorkerId.New(),
                "billing.close",
                subject,
                concurrency,
                new HashSet<WorkIdentifier> { identifier },
                1,
                "Billing:Invoices",
                WorkerState.Running,
                null,
                now,
                now,
                now),
            RecurrenceEnabled: true,
            ConcurrencyEnabled: true,
            ProfilingEnabled: true,
            OriginActorId: "owner");

        Assert.True(Matches(worker, new WorkerCriteria()));
        var mismatches = new WorkerCriteria[]
        {
            new(DefinitionNames: new HashSet<string> { "other" }),
            new(DefinitionName: "other"),
            new(Category: "Shipping"),
            new(SubjectId: new WorkSubjectId("invoice", "missing")),
            new(ConcurrencyKey: new WorkConcurrencyKey("tenant", "missing")),
            new(Identifier: new WorkIdentifier("order", "missing")),
            new(ActorId: "other"),
            new(States: new HashSet<WorkerState> { WorkerState.Paused }),
            new(Configuration: new WorkerConfigurationCriteria(RecurrenceEnabled: false)),
            new(Configuration: new WorkerConfigurationCriteria(ConcurrencyEnabled: false)),
            new(Configuration: new WorkerConfigurationCriteria(ProfilingEnabled: false)),
            new(CreatedFrom: now.AddSeconds(1)),
            new(CreatedTo: now.AddSeconds(-1)),
            new(UpdatedFrom: now.AddSeconds(1)),
            new(UpdatedTo: now.AddSeconds(-1)),
        };
        Assert.All(mismatches, criteria => Assert.False(Matches(worker, criteria)));
        Assert.True(Matches(worker, new WorkerCriteria(
            DefinitionNames: new HashSet<string> { "BILLING.CLOSE" },
            DefinitionName: "BILLING.CLOSE",
            Category: "billing",
            SubjectId: subject,
            ConcurrencyKey: concurrency,
            Identifier: identifier,
            ActorId: " owner ",
            States: new HashSet<WorkerState> { WorkerState.Running },
            Configuration: new WorkerConfigurationCriteria(true, true, true),
            CreatedFrom: now,
            CreatedTo: now,
            UpdatedFrom: now,
            UpdatedTo: now)));
    }

    [Fact]
    public void IterationMatchingRejectsEveryIndependentFilterDimension()
    {
        var now = DateTimeOffset.UtcNow;
        var subject = new WorkSubjectId("invoice", "1");
        var concurrency = new WorkConcurrencyKey("tenant", "2");
        var identifier = new WorkIdentifier("order", "3");
        var overview = new WorkerIterationOverviewItem(
            WorkerId.New(),
            4,
            "billing.close",
            "Billing:Invoices",
            WorkerState.Completed,
            WorkCompletionStatus.Completed,
            now,
            now,
            TimeSpan.FromSeconds(1),
            subject,
            concurrency,
            new HashSet<WorkIdentifier> { identifier });
        var iteration = new WorkerReadModelIteration(
            WorkDefinitionId.New(),
            new WorkerIterationReference(overview.WorkerId, overview.Sequence),
            overview);

        Assert.True(Matches(iteration, new WorkerIterationCriteria()));
        var mismatches = new WorkerIterationCriteria[]
        {
            new(WorkerId: WorkerId.New()),
            new(DefinitionNames: new HashSet<string> { "other" }),
            new(DefinitionName: "other"),
            new(Category: "Shipping"),
            new(SubjectId: new WorkSubjectId("invoice", "missing")),
            new(ConcurrencyKey: new WorkConcurrencyKey("tenant", "missing")),
            new(Identifier: new WorkIdentifier("order", "missing")),
            new(Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Failed }),
            new(StartedFrom: now.AddSeconds(1)),
            new(StartedTo: now.AddSeconds(-1)),
            new(CompletedFrom: now.AddSeconds(1)),
            new(CompletedTo: now.AddSeconds(-1)),
        };
        Assert.All(mismatches, criteria => Assert.False(Matches(iteration, criteria)));
        Assert.True(Matches(iteration, new WorkerIterationCriteria(
            WorkerId: overview.WorkerId,
            DefinitionNames: new HashSet<string> { "BILLING.CLOSE" },
            DefinitionName: "BILLING.CLOSE",
            Category: "billing",
            SubjectId: subject,
            ConcurrencyKey: concurrency,
            Identifier: identifier,
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed },
            StartedFrom: now,
            StartedTo: now,
            CompletedFrom: now,
            CompletedTo: now)));
    }

    [Fact]
    public void MatchingTreatsEmptyAndWhitespaceFiltersAsUnrestricted()
    {
        var now = DateTimeOffset.UtcNow;
        var worker = new WorkerReadModelWorker(
            WorkDefinitionId.New(),
            new WorkerOverviewItem(
                WorkerId.New(),
                "billing.close",
                null,
                null,
                new HashSet<WorkIdentifier>(),
                1,
                "Billing",
                WorkerState.Running,
                null,
                now,
                now,
                now),
            RecurrenceEnabled: false,
            ConcurrencyEnabled: false,
            ProfilingEnabled: false,
            OriginActorId: null);
        Assert.False(Matches(worker, new WorkerCriteria(
            DefinitionNames: new HashSet<string>())));
        Assert.False(Matches(worker, new WorkerCriteria(
            States: new HashSet<WorkerState>())));
        Assert.True(Matches(worker, new WorkerCriteria(
            DefinitionName: " ",
            Category: " ",
            ActorId: " ",
            Configuration: new WorkerConfigurationCriteria())));

        var overview = new WorkerIterationOverviewItem(
            worker.Id,
            1,
            worker.DefinitionName,
            worker.Category,
            worker.State,
            WorkCompletionStatus.Completed,
            now,
            now,
            TimeSpan.Zero,
            null,
            null,
            new HashSet<WorkIdentifier>());
        var iteration = new WorkerReadModelIteration(
            worker.DefinitionId,
            new WorkerIterationReference(worker.Id, 1),
            overview);
        Assert.False(Matches(iteration, new WorkerIterationCriteria(
            DefinitionNames: new HashSet<string>())));
        Assert.False(Matches(iteration, new WorkerIterationCriteria(
            Statuses: new HashSet<WorkCompletionStatus>())));
        Assert.True(Matches(iteration, new WorkerIterationCriteria(
            DefinitionName: " ",
            Category: " ")));
    }

    [Fact]
    public void InternalIterationSortingAndDefinitionStatusCoverEveryTerminalBranch()
    {
        var now = DateTimeOffset.UtcNow;
        var firstOverview = new WorkerIterationOverviewItem(
            WorkerId.New(), 1, "alpha", "A", WorkerState.Completed,
            WorkCompletionStatus.Completed, now.AddMinutes(-1), now,
            TimeSpan.FromSeconds(1), null, null, []);
        var secondOverview = firstOverview with
        {
            WorkerId = WorkerId.New(),
            Sequence = 2,
            DefinitionName = "beta",
            Status = WorkCompletionStatus.Failed,
            StartedAt = now,
            ExecutionDuration = TimeSpan.FromSeconds(2),
        };
        var values = new[]
        {
            new WorkerReadModelIteration(
                WorkDefinitionId.New(),
                new WorkerIterationReference(firstOverview.WorkerId, 1),
                firstOverview),
            new WorkerReadModelIteration(
                WorkDefinitionId.New(),
                new WorkerIterationReference(secondOverview.WorkerId, 2),
                secondOverview),
        };
        foreach (var sort in Enum.GetValues<WorkerIterationCriteriaSort>())
        foreach (var direction in Enum.GetValues<WorkCriteriaSortDirection>())
        {
            var sorted = InvokeStaticExact<IEnumerable<WorkerReadModelIteration>>(
                "Sort",
                [typeof(IEnumerable<WorkerReadModelIteration>), typeof(WorkerIterationCriteriaSort), typeof(WorkCriteriaSortDirection)],
                values,
                sort,
                direction);
            Assert.Equal(2, sorted.Count());
        }

        Assert.Equal(WorkDefinitionStatus.Inactive, Status(new WorkerRollup(0, 0, 0, 0, 0, 0, 0, 0, 0, null)));
        Assert.Equal(WorkDefinitionStatus.Inactive, Status(new WorkerRollup(2, 0, 0, 0, 0, 0, 0, 1, 1, now)));
        Assert.Equal(WorkDefinitionStatus.Critical, Status(new WorkerRollup(2, 1, 0, 0, 0, 0, 1, 0, 1, now)));
        Assert.Equal(WorkDefinitionStatus.NeedsAttention, Status(new WorkerRollup(2, 2, 0, 1, 0, 0, 1, 0, 0, now)));
        Assert.Equal(WorkDefinitionStatus.NeedsAttention, Status(new WorkerRollup(1, 1, 0, 0, 0, 1, 0, 0, 0, now)));
        Assert.Equal(WorkDefinitionStatus.Healthy, Status(new WorkerRollup(1, 1, 0, 1, 0, 0, 0, 0, 0, now)));
        Assert.Equal(WorkDefinitionStatus.Unknown, Status(new WorkerRollup(1, 0, 0, 0, 0, 0, 0, 0, 0, now)));
    }

    [Fact]
    public void CountWorkersAcrossUnrestrictedAndDefinitionScopedInputs()
    {
        var firstId = WorkDefinitionId.New();
        var secondId = WorkDefinitionId.New();
        var now = DateTimeOffset.UtcNow;
        WorkerReadModelWorker Worker(WorkDefinitionId definitionId, WorkerState state) => new(
            definitionId,
            new WorkerOverviewItem(
                WorkerId.New(),
                "counted.work",
                null,
                null,
                new HashSet<WorkIdentifier>(),
                1,
                "General",
                state,
                null,
                now,
                now,
                now),
            false,
            false,
            false,
            null);
        var workers = new[]
        {
            Worker(firstId, WorkerState.Running),
            Worker(secondId, WorkerState.Completed),
        };

        var all = InvokeStaticExact<Dictionary<WorkerState, int>>(
            "CountWorkersByState",
            [typeof(IEnumerable<WorkerReadModelWorker>), typeof(IReadOnlySet<WorkDefinitionId>)],
            workers,
            null);
        var scoped = InvokeStaticExact<Dictionary<WorkerState, int>>(
            "CountWorkersByState",
            [typeof(IEnumerable<WorkerReadModelWorker>), typeof(IReadOnlySet<WorkDefinitionId>)],
            workers,
            new HashSet<WorkDefinitionId> { firstId });

        Assert.Equal(2, all.Values.Sum());
        Assert.Equal(1, scoped[WorkerState.Running]);
        Assert.DoesNotContain(WorkerState.Completed, scoped.Keys);
    }

    private static WorkDefinitionStatus Status(WorkerRollup rollup)
        => InvokeStatic<WorkDefinitionStatus>("StatusFor", rollup);

    private static bool Matches(WorkDefinition definition, WorkDefinitionCriteria criteria)
        => InvokeStaticExact<bool>("Matches", [typeof(WorkDefinition), typeof(WorkDefinitionCriteria)], definition, criteria);

    private static bool Matches(WorkDefinition definition, WorkSystemCriteria criteria)
        => InvokeStaticExact<bool>("Matches", [typeof(WorkDefinition), typeof(WorkSystemCriteria)], definition, criteria);

    private static bool Matches(WorkerReadModelWorker worker, WorkerCriteria criteria)
        => InvokeStaticExact<bool>("Matches", [typeof(WorkerReadModelWorker), typeof(WorkerCriteria)], worker, criteria);

    private static bool Matches(WorkerReadModelIteration iteration, WorkerIterationCriteria criteria)
        => InvokeStaticExact<bool>(
            "Matches",
            [typeof(WorkerReadModelIteration), typeof(WorkerIterationCriteria)],
            iteration,
            criteria);

    private static IEnumerable<WorkerOverviewItem> SortWorkers(
        IEnumerable<WorkerOverviewItem> items,
        WorkerCriteriaSort sort,
        WorkCriteriaSortDirection direction)
        => InvokeStaticExact<IEnumerable<WorkerOverviewItem>>(
            "Sort",
            [typeof(IEnumerable<WorkerOverviewItem>), typeof(WorkerCriteriaSort), typeof(WorkCriteriaSortDirection)],
            items,
            sort,
            direction);

    private static IEnumerable<WorkerIterationOverviewItem> SortIterations(
        IEnumerable<WorkerIterationOverviewItem> items,
        WorkerIterationCriteriaSort sort,
        WorkCriteriaSortDirection direction)
        => InvokeStaticExact<IEnumerable<WorkerIterationOverviewItem>>(
            "Sort",
            [typeof(IEnumerable<WorkerIterationOverviewItem>), typeof(WorkerIterationCriteriaSort), typeof(WorkCriteriaSortDirection)],
            items,
            sort,
            direction);

    private static T InvokeStatic<T>(string name, params object?[] arguments)
    {
        var method = typeof(WorkSystemReadModelQueryService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        return (T)method.Invoke(null, arguments)!;
    }

    private static T InvokeStaticExact<T>(string name, Type[] parameterTypes, params object?[] arguments)
        => (T)typeof(WorkSystemReadModelQueryService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, parameterTypes)!
            .Invoke(null, arguments)!;
}
