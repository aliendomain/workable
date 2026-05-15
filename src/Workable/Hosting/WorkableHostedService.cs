using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed class WorkableHostedService(
    IWorkSystemRegistry registry,
    IEnumerable<WorkSystemRegistration> registrations,
    ILogger<WorkableHostedService> logger) : IHostedService
{
    private static readonly IReadOnlySet<WorkerState> ShutdownWorkerStates = new HashSet<WorkerState>
    {
        WorkerState.Queued,
        WorkerState.Running,
        WorkerState.Waiting,
        WorkerState.Retrying,
    };

    private const int ShutdownWorkerLogLimit = 50;

    async Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        var autoStartIds = registrations
            .Where(registration => registration.StartWithHost)
            .Select(registration => registration.Id)
            .ToHashSet();

        foreach (var system in registry.Systems.Where(system => autoStartIds.Contains(system.Id)))
        {
            await system.Start(cancellationToken);
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        var systems = registry.Systems.ToList();
        var shutdownPlans = await Task.WhenAll(systems.Select(CreateShutdownPlan));
        this.LogShutdownStart(shutdownPlans);
        var shutdownResults = await Task.WhenAll(shutdownPlans.Select(this.StopSystem));
        this.LogShutdownResults(shutdownResults);
    }

    private async Task<SystemShutdownResult> StopSystem(SystemShutdownPlan plan)
    {
        try
        {
            return new SystemShutdownResult(
                plan,
                await plan.System.Stop(CancellationToken.None),
                Exception: null);
        }
        catch (Exception exception)
        {
            return new SystemShutdownResult(plan, Result: null, exception);
        }
    }

    private static async Task<SystemShutdownPlan> CreateShutdownPlan(IWorkSystem system)
        => new(
            system,
            FormatSystemName(system),
            GetShutdownGracePeriod(system),
            await GetShutdownWorkers(system));

    private void LogShutdownStart(IReadOnlyList<SystemShutdownPlan> plans)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var workerCount = plans.Sum(plan => plan.Workers.Count);
        var shutdownSummary = string.Join(
            Environment.NewLine,
            [
                "Workable shutdown started:",
                $"  Systems ({plans.Count}): {string.Join(", ", plans.Select(plan => plan.SystemName))}",
                $"  Workers to stop: {workerCount}",
                $"  Grace periods: {string.Join(" | ", plans.Select(FormatPlanSummary))}"
            ]);

        logger.LogInformation("{ShutdownSummary}", shutdownSummary);

        var plansWithWorkers = plans.Where(plan => plan.Workers.Count > 0).ToList();
        if (plansWithWorkers.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Stopping workers: {WorkersBySystem}",
            string.Join(" | ", plansWithWorkers.Select(plan =>
                $"{plan.SystemName}: {FormatWorkers(plan.Workers)}")));
    }

    private void LogShutdownResults(IReadOnlyList<SystemShutdownResult> results)
    {
        foreach (var failed in results.Where(result => result.Exception is not null))
        {
            logger.LogError(
                failed.Exception,
                "Workable system {SystemName} failed during shutdown.",
                failed.Plan.SystemName);
        }

        var successful = results
            .Where(result => result.Result is not null)
            .ToList();
        var forceCanceled = successful
            .Where(result => result.Result!.ForceCanceledWorkerSummaries.Count > 0)
            .ToList();

        if (forceCanceled.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "Force-canceled {WorkerCount} worker(s): {WorkersBySystem}",
                forceCanceled.Sum(result => result.Result!.ForceCanceledWorkerSummaries.Count),
                string.Join(" | ", forceCanceled.Select(result =>
                    $"{result.Plan.SystemName}: {FormatWorkers(result.Result!.ForceCanceledWorkerSummaries)}")));
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Workable shutdown complete: {SystemCount} system(s), {WorkerCount} cooperative cancellation(s).",
                successful.Count,
                successful.Sum(result => result.Result!.CancellationRequestedWorkers.Count));
        }
    }

    private static async Task<IReadOnlyList<WorkSystemShutdownWorker>> GetShutdownWorkers(IWorkSystem system)
    {
        var workers = new List<WorkSystemShutdownWorker>();
        while (true)
        {
            var result = await system.Query.Workers(new WorkerCriteria(
                    States: ShutdownWorkerStates,
                    Sort: WorkerCriteriaSort.CreatedAt,
                    Direction: WorkCriteriaSortDirection.Ascending,
                    Skip: workers.Count,
                    Take: WorkerCriteria.MaximumTake), cancellationToken: CancellationToken.None);

            workers.AddRange(result.Workers.Select(WorkSystemShutdownWorker.From));
            if (result.Workers.Count == 0 ||
                workers.Count >= result.TotalCount ||
                result.Workers.Count < WorkerCriteria.MaximumTake)
            {
                return workers;
            }
        }
    }

    private static string FormatWorkers(IReadOnlyList<WorkSystemShutdownWorker> workers)
    {
        var formatted = workers
            .Take(ShutdownWorkerLogLimit)
            .GroupBy(worker => worker.DefinitionName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() == 1
                ? group.Key
                : $"{group.Key} x{group.Count()}");
        return AppendAdditionalCount(string.Join("; ", formatted), workers.Count);
    }

    private static string AppendAdditionalCount(string workers, int count)
    {
        var remaining = count - ShutdownWorkerLogLimit;
        return remaining > 0
            ? $"{workers}; and {remaining} more"
            : workers;
    }

    private static string FormatSystemName(IWorkSystem system)
        => string.IsNullOrWhiteSpace(system.Name)
            ? "default"
            : system.Name;

    private static string FormatPlanSummary(SystemShutdownPlan plan)
        => $"{plan.SystemName} {FormatDuration(plan.ShutdownGracePeriod)}";

    private static TimeSpan? GetShutdownGracePeriod(IWorkSystem system)
        => system is IWorkSystemShutdownMetadata metadata
            ? metadata.ShutdownGracePeriod
            : null;

    private static string FormatDuration(TimeSpan? duration)
        => duration is { } value
            ? value.ToString("g")
            : "unknown";

    private sealed record SystemShutdownPlan(
        IWorkSystem System,
        string SystemName,
        TimeSpan? ShutdownGracePeriod,
        IReadOnlyList<WorkSystemShutdownWorker> Workers);

    private sealed record SystemShutdownResult(
        SystemShutdownPlan Plan,
        WorkSystemStopResult? Result,
        Exception? Exception);
}
