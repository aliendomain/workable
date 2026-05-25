using System.Collections.Concurrent;
using Workable;

namespace SampleHost.Demo;

public sealed record DemoRecurringIterationInput(
    int MaximumIterationDurationMilliseconds);

public sealed record DemoRecurringIterationOutput(
    string Mode,
    int LogEntriesWritten,
    int RecoveredAfterTransientFailures,
    DateTimeOffset CompletedAt);

public sealed class DemoRecurringIterationWork(
    DemoRecurringIterationPlanStore plans,
    ILogger<DemoRecurringIterationWork> logger) : IWorkExecutor<DemoRecurringIterationInput, DemoRecurringIterationOutput>
{
    private const int SuccessLogEntryCount = 10;
    private const int MaximumTransientRecoveryFailures = 4;

    public async Task<WorkExecutionResult<DemoRecurringIterationOutput>> Execute(
        IWorkExecutionContext context,
        DemoRecurringIterationInput input,
        CancellationToken cancellationToken)
    {
        var plan = plans.GetOrCreate(context.WorkerId, MaximumTransientRecoveryFailures);
        return plan.Mode switch
        {
            DemoRecurringIterationMode.NormalSuccess => await this.ExecuteNormalSuccess(context, input, cancellationToken),
            DemoRecurringIterationMode.NonTransientFailure => await this.ExecuteNonTransientFailure(context, input, cancellationToken),
            DemoRecurringIterationMode.TransientRecovery => await this.ExecuteTransientRecovery(context, input, plan, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported recurring iteration mode '{plan.Mode}'."),
        };
    }

    private async Task<WorkExecutionResult<DemoRecurringIterationOutput>> ExecuteNormalSuccess(
        IWorkExecutionContext context,
        DemoRecurringIterationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.WriteLogs(
                context,
                "normal-success",
                SuccessLogEntryCount,
                attemptNumber: 1,
                totalAttempts: 1,
                targetDurationMilliseconds: Random.Shared.Next(input.MaximumIterationDurationMilliseconds + 1),
                cancellationToken);

            return WorkExecutionResult<DemoRecurringIterationOutput>.Success(
                new DemoRecurringIterationOutput(
                    "normal-success",
                    SuccessLogEntryCount,
                    RecoveredAfterTransientFailures: 0,
                    DateTimeOffset.UtcNow),
                [WorkMessage.Info("sample.demo.iteration.completed", "The recurring sample iteration completed normally.")]);
        }
        finally
        {
            plans.Complete(context.WorkerId);
        }
    }

    private async Task<WorkExecutionResult<DemoRecurringIterationOutput>> ExecuteNonTransientFailure(
        IWorkExecutionContext context,
        DemoRecurringIterationInput input,
        CancellationToken cancellationToken)
    {
        var logEntryCount = Random.Shared.Next(3, 9);
        try
        {
            await this.WriteLogs(
                context,
                "non-transient-failure",
                logEntryCount,
                attemptNumber: 1,
                totalAttempts: 1,
                targetDurationMilliseconds: Random.Shared.Next(input.MaximumIterationDurationMilliseconds + 1),
                cancellationToken);
        }
        finally
        {
            plans.Complete(context.WorkerId);
        }

        var failureText = "Recurring sample hit a non-transient failure.";
        var useContextFailure = Random.Shared.Next(2) == 0;
        logger.LogError(
            useContextFailure
                ? "non-transient-failure iteration reported a non-transient failure through the execution context on attempt {AttemptNumber} of {TotalAttempts}."
                : "non-transient-failure iteration encountered a non-transient error on attempt {AttemptNumber} of {TotalAttempts}.",
            1,
            1);
        if (useContextFailure)
        {
            context.Fail("sample.demo.iteration.failed", failureText, "execution");
            return WorkExecutionResult<DemoRecurringIterationOutput>.Success(output: null);
        }

        throw CreateSampleNonTransientException(failureText);
    }

    private async Task<WorkExecutionResult<DemoRecurringIterationOutput>> ExecuteTransientRecovery(
        IWorkExecutionContext context,
        DemoRecurringIterationInput input,
        DemoRecurringIterationPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.ShouldFailTransiently)
        {
            var logEntryCount = Random.Shared.Next(2, 8);
            await this.WriteLogs(
                context,
                "transient-failure",
                logEntryCount,
                plan.AttemptNumber,
                plan.PlannedTransientFailures + 1,
                targetDurationMilliseconds: Random.Shared.Next(input.MaximumIterationDurationMilliseconds + 1),
                cancellationToken);

            plan.RecordTransientFailure();
            logger.LogError(
                "transient-failure iteration encountered a transient error on attempt {AttemptNumber} of {TotalAttempts}.",
                plan.AttemptNumber - 1,
                plan.PlannedTransientFailures + 1);
            throw CreateSampleTransientException(
                $"Recurring sample hit a transient failure on attempt {plan.AttemptNumber - 1}.");
        }

        try
        {
            await this.WriteLogs(
                context,
                "transient-recovery-success",
                SuccessLogEntryCount,
                plan.AttemptNumber,
                plan.PlannedTransientFailures + 1,
                targetDurationMilliseconds: Random.Shared.Next(input.MaximumIterationDurationMilliseconds + 1),
                cancellationToken);

            return WorkExecutionResult<DemoRecurringIterationOutput>.Success(
                new DemoRecurringIterationOutput(
                    "transient-recovery-success",
                    SuccessLogEntryCount,
                    plan.PlannedTransientFailures,
                    DateTimeOffset.UtcNow),
                [WorkMessage.Info(
                    "sample.demo.iteration.recovered",
                    $"The recurring sample recovered after {plan.PlannedTransientFailures} transient failure(s).")]);
        }
        finally
        {
            plans.Complete(context.WorkerId);
        }
    }

    private async Task WriteLogs(
        IWorkExecutionContext context,
        string mode,
        int logEntryCount,
        int attemptNumber,
        int totalAttempts,
        int targetDurationMilliseconds,
        CancellationToken cancellationToken)
    {
        var remainingDelayMilliseconds = targetDurationMilliseconds;
        for (var index = 1; index <= logEntryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 10 == 0)
            {
                logger.LogWarning(
                    "{Mode} iteration wrote log entry {EntryIndex} of {EntryCount} on attempt {AttemptNumber} of {TotalAttempts}.",
                    mode,
                    index,
                    logEntryCount,
                    attemptNumber,
                    totalAttempts);
            }
            else
            {
                logger.LogInformation(
                    "{Mode} iteration wrote log entry {EntryIndex} of {EntryCount} on attempt {AttemptNumber} of {TotalAttempts}.",
                    mode,
                    index,
                    logEntryCount,
                    attemptNumber,
                    totalAttempts);
            }

            var remainingEntries = logEntryCount - index + 1;
            var delayMilliseconds = remainingEntries == 1
                ? remainingDelayMilliseconds
                : Random.Shared.Next(remainingDelayMilliseconds + 1);
            remainingDelayMilliseconds -= delayMilliseconds;

            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
            }
        }

        logger.LogInformation(
            "{Mode} iteration finished writing {EntryCount} log entries for worker {WorkerId}.",
            mode,
            logEntryCount,
            context.WorkerId.Value);
    }

    private static DemoRecurringNonTransientException CreateSampleNonTransientException(string message)
    {
        try
        {
            TriggerFrameworkFailure();
            throw new InvalidOperationException("The sample lab expected framework failure simulation to throw.");
        }
        catch (Exception exception)
        {
            throw WrapNonTransientFailure(message, exception);
        }
    }

    private static DemoRecurringTransientException CreateSampleTransientException(string message)
    {
        try
        {
            TriggerFrameworkFailure();
            throw new TimeoutException("The sample lab expected framework failure simulation to throw.");
        }
        catch (Exception exception)
        {
            throw WrapTransientFailure(message, exception);
        }
    }

    private static DemoRecurringNonTransientException WrapNonTransientFailure(string message, Exception innerException)
        => new(message, innerException);

    private static DemoRecurringTransientException WrapTransientFailure(string message, Exception innerException)
        => new(message, innerException);

    private static void TriggerFrameworkFailure()
    {
        _ = Convert.FromBase64String("not-base64!");
    }
}

public sealed class DemoRecurringIterationPlanStore
{
    private readonly ConcurrentDictionary<WorkerId, DemoRecurringIterationPlan> plans = [];

    public DemoRecurringIterationPlan GetOrCreate(WorkerId workerId, int maximumTransientRecoveryFailures)
        => this.plans.GetOrAdd(
            workerId,
            static (_, maxFailures) => DemoRecurringIterationPlan.Create(maxFailures),
            maximumTransientRecoveryFailures);

    public void Complete(WorkerId workerId)
        => this.Forget(workerId);

    public void Forget(WorkerId workerId)
        => this.plans.TryRemove(workerId, out _);
}

public sealed class DemoRecurringTransientException(string message, Exception? innerException = null) : TimeoutException(message, innerException);

public sealed class DemoRecurringNonTransientException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

public enum DemoRecurringIterationMode
{
    NormalSuccess,
    NonTransientFailure,
    TransientRecovery,
}

public sealed class DemoRecurringIterationPlan
{
    private DemoRecurringIterationPlan(
        DemoRecurringIterationMode mode,
        int plannedTransientFailures)
    {
        this.Mode = mode;
        this.PlannedTransientFailures = plannedTransientFailures;
        this.RemainingTransientFailures = plannedTransientFailures;
    }

    public DemoRecurringIterationMode Mode { get; }

    public int PlannedTransientFailures { get; }

    public int RemainingTransientFailures { get; private set; }

    public int AttemptNumber { get; private set; } = 1;

    public bool ShouldFailTransiently
        => this.Mode == DemoRecurringIterationMode.TransientRecovery && this.RemainingTransientFailures > 0;

    public void RecordTransientFailure()
    {
        if (!this.ShouldFailTransiently)
        {
            return;
        }

        this.RemainingTransientFailures--;
        this.AttemptNumber++;
    }

    public static DemoRecurringIterationPlan Create(int maximumTransientRecoveryFailures)
    {
        var roll = Random.Shared.NextDouble();
        if (roll < 0.90d)
        {
            return new DemoRecurringIterationPlan(DemoRecurringIterationMode.NormalSuccess, plannedTransientFailures: 0);
        }

        if (roll < 0.95d)
        {
            return new DemoRecurringIterationPlan(DemoRecurringIterationMode.NonTransientFailure, plannedTransientFailures: 0);
        }

        return new DemoRecurringIterationPlan(
            DemoRecurringIterationMode.TransientRecovery,
            plannedTransientFailures: Random.Shared.Next(1, maximumTransientRecoveryFailures + 1));
    }
}
