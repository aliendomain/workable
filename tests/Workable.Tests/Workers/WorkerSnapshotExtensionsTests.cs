using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkerLifecycle")]
public sealed class WorkerSnapshotExtensionsTests
{
    [Fact]
    public void GetMergedIterationsPrefersFinalIterationAndMergesRetainedData()
    {
        var worker = CreateWorkerSnapshot(
            iterations:
            [
                new WorkerIterationSnapshot(
                    Sequence: 1,
                    StartedAt: DateTimeOffset.UtcNow.AddSeconds(-10),
                    CompletedAt: DateTimeOffset.UtcNow.AddSeconds(-5),
                    ExecutionDuration: TimeSpan.FromSeconds(5),
                    Status: WorkCompletionStatus.Completed,
                    Output: WorkOutput.FromJson("{\"done\":true}"),
                    Messages:
                    [
                        WorkMessage.Info("retained.info", "Retained info."),
                    ])
                {
                    Logs =
                    [
                        new WorkerLogEntry(
                            DateTimeOffset.UtcNow.AddSeconds(-6),
                            WorkerId.New(),
                            WorkDefinitionId.New(),
                            "tests.retained",
                            LogLevel.Information,
                            new EventId(1, "retained"),
                            "retained info"),
                    ],
                },
            ],
            currentIteration: new WorkerIterationSnapshot(
                Sequence: 1,
                StartedAt: DateTimeOffset.UtcNow.AddSeconds(-10),
                CompletedAt: DateTimeOffset.UtcNow.AddSeconds(-4),
                ExecutionDuration: TimeSpan.FromSeconds(6),
                Status: WorkCompletionStatus.Executing,
                Output: null,
                Messages:
                [
                    WorkMessage.Warning("current.warning", "Current warning."),
                ])
            {
                Logs =
                [
                    new WorkerLogEntry(
                        DateTimeOffset.UtcNow.AddSeconds(-4),
                        WorkerId.New(),
                        WorkDefinitionId.New(),
                        "tests.current",
                        LogLevel.Warning,
                        new EventId(2, "current"),
                        "current warning"),
                ],
            });

        var merged = worker.GetMergedIterations();
        var iteration = Assert.Single(merged);

        Assert.Equal(WorkCompletionStatus.Completed, iteration.Status);
        Assert.Equal(2, iteration.Messages.Count);
        Assert.Equal(2, iteration.Logs.Count);
        Assert.NotNull(iteration.Output);
        var latest = Assert.IsType<WorkerIterationSnapshot>(worker.GetLatestKnownIteration());
        Assert.Equal(iteration.Sequence, latest.Sequence);
        Assert.Equal(iteration.Status, latest.Status);
        Assert.Equal(iteration.Messages.Count, latest.Messages.Count);
    }

    [Fact]
    public void FailurePropertyExtractsStructuredExceptionDetails()
    {
        var iteration = new WorkerIterationSnapshot(
            Sequence: 7,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-8),
            CompletedAt: DateTimeOffset.UtcNow.AddSeconds(-2),
            ExecutionDuration: TimeSpan.FromSeconds(6),
            Status: WorkCompletionStatus.Failed,
            Output: null,
            Messages:
            [
                new WorkMessage(
                    "workable.execution.exception",
                    WorkMessageSeverity.Error,
                    "Boom",
                    "execution.exception",
                    new Dictionary<string, object?>
                    {
                        ["failureSource"] = "executionContext",
                        ["exceptionType"] = "System.InvalidOperationException",
                        ["exceptionMessage"] = "Boom",
                        ["exceptionStackTrace"] = "at Tests.Throw()",
                    }),
            ]);

        var failure = Assert.IsType<WorkerIterationFailure>(iteration.Failure);

        Assert.Equal(WorkerIterationFailureKind.Exception, failure.Kind);
        Assert.Equal("System.InvalidOperationException", failure.ExceptionType);
        Assert.Equal("Boom", failure.Message);
        Assert.True(failure.DeclaredByWork);
        Assert.Equal(iteration.CompletedAt, iteration.SettledAt);
        Assert.Equal(iteration.ExecutionDuration, iteration.SettledExecutionDuration);
    }

    [Fact]
    public void GetActivityEventsIncludesAcceptedStateChangesAndFailureIterations()
    {
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            actor: new WorkActor("user-1", "Test User"));
        var iteration = new WorkerIterationSnapshot(
            Sequence: 3,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-30),
            CompletedAt: DateTimeOffset.UtcNow.AddSeconds(-20),
            ExecutionDuration: TimeSpan.FromSeconds(10),
            Status: WorkCompletionStatus.Failed,
            Output: null,
            Messages:
            [
                WorkMessage.Error("failure.code", "The iteration failed."),
            ]);
        var worker = CreateWorkerSnapshot(
            state: WorkerState.Failed,
            stateSequence: 2,
            iterations: [iteration],
            actionHistory:
            [
                new WorkerActionHistoryEntry(
                    OccurredAt: DateTimeOffset.UtcNow.AddSeconds(-25),
                    Kind: WorkerActionHistoryKind.WorkerAction,
                    Action: WorkAction.Cancel,
                    Status: WorkActionStatus.Accepted,
                    RequestContext: requestContext,
                    Revision: 1,
                    StateSequence: 1,
                    State: WorkerState.Canceled,
                    Messages: [],
                    IterationSequence: 3),
            ]);

        var events = worker.GetActivityEvents();

        Assert.Contains(events, item =>
            item.Kind == WorkerActivityEventKind.ActionRequest &&
            item.Category == WorkerActivityEventCategory.UserAction &&
            item.Action == WorkAction.Cancel &&
            item.Sequence == 3);
        Assert.Contains(events, item =>
            item.Kind == WorkerActivityEventKind.StateChange &&
            item.State == WorkerState.Canceled);
        Assert.Contains(events, item =>
            item.Kind == WorkerActivityEventKind.Iteration &&
            item.Category == WorkerActivityEventCategory.Failure &&
            item.Sequence == 3);
    }

    [Fact]
    public void ConfigurationDifferenceCounterIgnoresInvocationAndCountsProfilingDifference()
    {
        var currentConfiguration = WorkConfiguration.Default with
        {
            Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.SignalR),
        };
        var defaultConfiguration = WorkConfiguration.Default with
        {
            Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.DotNet),
        };

        var invocationOnlyDifferences = WorkerConfigurationDifferenceCounter.CountDifferences(
            new WorkerOptions(ProfilingEnabled: false),
            currentConfiguration,
            new WorkerOptions(ProfilingEnabled: false),
            defaultConfiguration);
        var profilingDifferences = WorkerConfigurationDifferenceCounter.CountDifferences(
            new WorkerOptions(ProfilingEnabled: true),
            currentConfiguration,
            new WorkerOptions(ProfilingEnabled: false),
            defaultConfiguration);

        Assert.Equal(0, invocationOnlyDifferences);
        Assert.Equal(1, profilingDifferences);
    }

    private static WorkerSnapshot CreateWorkerSnapshot(
        WorkerState state = WorkerState.Running,
        long stateSequence = 1,
        IReadOnlyList<WorkerIterationSnapshot>? iterations = null,
        WorkerIterationSnapshot? currentIteration = null,
        IReadOnlyList<WorkerActionHistoryEntry>? actionHistory = null)
    {
        var workerId = WorkerId.New();
        var definitionId = WorkDefinitionId.New();
        var now = DateTimeOffset.UtcNow;
        return new WorkerSnapshot(
            workerId,
            Revision: 1,
            StateSequence: stateSequence,
            DefinitionId: definitionId,
            DefinitionName: "tests.worker.snapshot",
            DefinitionCategory: "Tests",
            SubjectId: null,
            ConcurrencyKey: null,
            Identifiers: new HashSet<WorkIdentifier>(),
            RequestContext: WorkRequestContext.Create(WorkInvocationChannel.DotNet),
            State: state,
            Input: null,
            Output: null,
            Options: WorkerOptions.Default,
            Configuration: WorkConfiguration.Default,
            Messages: [],
            InterruptionReason: null,
            CreatedAt: now.AddMinutes(-5),
            StateChangedAt: now.AddMinutes(-1),
            UpdatedAt: now)
        {
            ActionHistory = actionHistory ?? [],
            CurrentIteration = currentIteration,
            CurrentIterationSequence = currentIteration?.Sequence,
            Iterations = iterations ?? [],
            LastIteration = iterations?.OrderByDescending(iteration => iteration.Sequence).FirstOrDefault(),
            LastIterationSequence = iterations?.OrderByDescending(iteration => iteration.Sequence).FirstOrDefault()?.Sequence,
        };
    }
}
