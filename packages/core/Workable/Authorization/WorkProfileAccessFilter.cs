namespace Workable;

internal static class WorkProfileAccessFilter
{
    public static WorkerSnapshot Apply(WorkerSnapshot worker, bool canViewDiagnostics)
        => canViewDiagnostics ? worker : WithoutProfiles(worker);

    public static WorkerIterationSnapshot Apply(
        WorkerIterationSnapshot iteration,
        bool canViewDiagnostics)
        => canViewDiagnostics ? iteration : WithoutProfile(iteration);

    public static WorkCompletion Apply(WorkCompletion completion, bool canViewDiagnostics)
        => canViewDiagnostics || completion.Worker is null
            ? completion
            : completion with { Worker = WithoutProfiles(completion.Worker) };

    public static WorkCompletion<TOutput> Apply<TOutput>(
        WorkCompletion<TOutput> completion,
        bool canViewDiagnostics)
        => canViewDiagnostics || completion.Worker is null
            ? completion
            : completion with { Worker = WithoutProfiles(completion.Worker) };

    public static WorkActionOutcome Apply(
        WorkActionOutcome outcome,
        bool canViewDiagnostics)
        => canViewDiagnostics || outcome.Worker is null
            ? outcome
            : outcome with { Worker = WithoutProfiles(outcome.Worker) };

    public static WorkerBulkActionOutcome Apply(
        WorkerBulkActionOutcome outcome,
        bool canViewDiagnostics)
        => canViewDiagnostics
            ? outcome
            : outcome with
            {
                Outcomes = [.. outcome.Outcomes.Select(item => Apply(item, canViewDiagnostics: false))],
            };

    public static WorkSystemStopResult Apply(
        WorkSystemStopResult result,
        bool canViewDiagnostics)
        => canViewDiagnostics
            ? result
            : result with
            {
                ForceInterruptedWorkers = WithoutProfiles(result.ForceInterruptedWorkers),
                CancellationRequestedWorkers = WithoutProfiles(result.CancellationRequestedWorkers),
            };

    private static WorkerSnapshot WithoutProfiles(WorkerSnapshot worker)
    {
        var iterations = WithoutProfiles(worker.Iterations);
        var currentIteration = worker.CurrentIteration is null
            ? null
            : WithoutProfile(worker.CurrentIteration);
        var lastIteration = worker.LastIteration is null
            ? null
            : WithoutProfile(worker.LastIteration);
        if (worker.Profile is null &&
            ReferenceEquals(iterations, worker.Iterations) &&
            ReferenceEquals(currentIteration, worker.CurrentIteration) &&
            ReferenceEquals(lastIteration, worker.LastIteration))
        {
            return worker;
        }

        return worker with
        {
            Profile = null,
            Iterations = iterations,
            CurrentIteration = currentIteration,
            LastIteration = lastIteration,
        };
    }

    private static IReadOnlyList<WorkerSnapshot> WithoutProfiles(
        IReadOnlyList<WorkerSnapshot> workers)
    {
        List<WorkerSnapshot>? filtered = null;
        for (var index = 0; index < workers.Count; index++)
        {
            var worker = workers[index];
            var sanitized = WithoutProfiles(worker);
            if (filtered is null && ReferenceEquals(sanitized, worker))
            {
                continue;
            }

            filtered ??= [.. workers.Take(index)];
            filtered.Add(sanitized);
        }

        return filtered ?? workers;
    }

    private static IReadOnlyList<WorkerIterationSnapshot> WithoutProfiles(
        IReadOnlyList<WorkerIterationSnapshot> iterations)
    {
        List<WorkerIterationSnapshot>? filtered = null;
        for (var index = 0; index < iterations.Count; index++)
        {
            var iteration = iterations[index];
            var sanitized = WithoutProfile(iteration);
            if (filtered is null && ReferenceEquals(sanitized, iteration))
            {
                continue;
            }

            filtered ??= [.. iterations.Take(index)];
            filtered.Add(sanitized);
        }

        return filtered ?? iterations;
    }

    private static WorkerIterationSnapshot WithoutProfile(WorkerIterationSnapshot iteration)
        => iteration.Profile is null ? iteration : iteration with { Profile = null };
}
