using Microsoft.Extensions.Logging;
using System.Text.Json;
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
                new WorkerActionHistoryEntry(
                    OccurredAt: DateTimeOffset.UtcNow.AddSeconds(-24),
                    Kind: WorkerActionHistoryKind.WorkerAction,
                    Action: WorkAction.Pause,
                    Status: WorkActionStatus.Accepted,
                    RequestContext: requestContext,
                    Revision: 2,
                    StateSequence: 1,
                    State: WorkerState.Paused,
                    Messages: [],
                    IterationSequence: 3),
                new WorkerActionHistoryEntry(
                    OccurredAt: DateTimeOffset.UtcNow.AddSeconds(-23),
                    Kind: WorkerActionHistoryKind.WorkerAction,
                    Action: WorkAction.Start,
                    Status: WorkActionStatus.Invalid,
                    RequestContext: requestContext,
                    Revision: 3,
                    StateSequence: 1,
                    State: WorkerState.Paused,
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
            item.Kind == WorkerActivityEventKind.StateChange &&
            item.State == WorkerState.Paused);
        Assert.Contains(events, item =>
            item.Kind == WorkerActivityEventKind.Iteration &&
            item.Category == WorkerActivityEventCategory.Failure &&
            item.Sequence == 3);
    }

    [Fact]
    public void GetMergedIterationsPrefersTheLatestTerminalSnapshot()
    {
        var earlier = new WorkerIterationSnapshot(
            4,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromMinutes(1),
            WorkCompletionStatus.Failed,
            WorkOutput.FromValue("earlier"),
            []);
        var later = earlier with
        {
            CompletedAt = DateTimeOffset.UtcNow,
            Status = WorkCompletionStatus.Completed,
            Output = WorkOutput.FromValue("later"),
        };
        var worker = CreateWorkerSnapshot(iterations: [earlier], currentIteration: later);

        var merged = Assert.Single(worker.GetMergedIterations());

        Assert.Equal(WorkCompletionStatus.Completed, merged.Status);
        Assert.Equal("later", merged.Output?.ToValue<string>());
    }

    [Fact]
    public void GetMergedIterationsCoversExecutingTerminalTieAndNullCollectionBoundaries()
    {
        var now = DateTimeOffset.UtcNow;
        var executing = CreateIteration(
            sequence: 1,
            status: WorkCompletionStatus.Executing,
            startedAt: default,
            completedAt: default,
            duration: default,
            messages: null,
            logs: null,
            output: null,
            attemptCount: 1);
        var terminal = CreateIteration(
            sequence: 1,
            status: WorkCompletionStatus.Completed,
            startedAt: now.AddMinutes(-2),
            completedAt: now,
            duration: TimeSpan.FromSeconds(4),
            messages: [WorkMessage.Info("same", "retained", target: null)],
            logs: [],
            output: WorkOutput.FromValue("terminal"),
            attemptCount: 2);

        var terminalPreferredFromRight = Assert.Single(CreateWorkerSnapshot(
            iterations: [executing],
            currentIteration: terminal).GetMergedIterations());
        var terminalPreferredFromLeft = Assert.Single(CreateWorkerSnapshot(
            iterations: [terminal],
            currentIteration: executing).GetMergedIterations());

        Assert.Equal(WorkCompletionStatus.Completed, terminalPreferredFromRight.Status);
        Assert.Equal(WorkCompletionStatus.Completed, terminalPreferredFromLeft.Status);
        Assert.Equal(now, terminalPreferredFromRight.CompletedAt);
        Assert.Equal(TimeSpan.FromSeconds(4), terminalPreferredFromLeft.ExecutionDuration);
        Assert.Equal(2, terminalPreferredFromRight.AttemptCount);
        Assert.Equal("terminal", terminalPreferredFromLeft.Output?.ToValue<string>());
        Assert.Equal(terminal.StartedAt, terminalPreferredFromRight.StartedAt);
        Assert.Single(terminalPreferredFromRight.Messages);
        Assert.Empty(terminalPreferredFromRight.Logs);

        var earlierCompleted = terminal with
        {
            CompletedAt = now.AddMinutes(-1),
            Output = WorkOutput.FromValue("earlier"),
        };
        var laterCompleted = terminal with
        {
            CompletedAt = now.AddMinutes(1),
            Output = WorkOutput.FromValue("later"),
        };
        var laterPreferred = Assert.Single(CreateWorkerSnapshot(
            iterations: [laterCompleted],
            currentIteration: earlierCompleted).GetMergedIterations());
        Assert.Equal("later", laterPreferred.Output?.ToValue<string>());

        var sameCompletionLeft = earlierCompleted with { Sequence = 3, Output = WorkOutput.FromValue("left") };
        var sameCompletionRight = earlierCompleted with { Sequence = 3, Output = WorkOutput.FromValue("right") };
        var rightTiePreferred = Assert.Single(CreateWorkerSnapshot(
            iterations: [sameCompletionLeft],
            currentIteration: sameCompletionRight).GetMergedIterations());
        Assert.Equal("right", rightTiePreferred.Output?.ToValue<string>());
    }

    [Fact]
    public void GetMergedIterationsFallsBackFromPreferredNullOutputAndDefaultStart()
    {
        var now = DateTimeOffset.UtcNow;
        var preferredTerminal = CreateIteration(
            8,
            WorkCompletionStatus.Completed,
            startedAt: default,
            completedAt: now,
            duration: TimeSpan.FromSeconds(5),
            messages: [],
            logs: [],
            output: null,
            attemptCount: 2);
        var secondaryTerminal = preferredTerminal with
        {
            StartedAt = now.AddMinutes(-1),
            CompletedAt = now.AddSeconds(-1),
            Output = WorkOutput.FromValue("secondary"),
        };
        var mergedTerminal = Assert.Single(CreateWorkerSnapshot(
            iterations: [preferredTerminal],
            currentIteration: secondaryTerminal).GetMergedIterations());
        Assert.Equal(secondaryTerminal.StartedAt, mergedTerminal.StartedAt);
        Assert.Equal("secondary", mergedTerminal.Output?.ToValue<string>());

        var preferredExecuting = preferredTerminal with
        {
            Status = WorkCompletionStatus.Executing,
            CompletedAt = now,
        };
        var secondaryExecuting = preferredExecuting with
        {
            CompletedAt = now.AddSeconds(-1),
            ExecutionDuration = TimeSpan.FromSeconds(3),
        };
        var mergedExecuting = Assert.Single(CreateWorkerSnapshot(
            iterations: [preferredExecuting],
            currentIteration: secondaryExecuting).GetMergedIterations());
        Assert.Equal(secondaryExecuting.CompletedAt, mergedExecuting.CompletedAt);
    }

    [Fact]
    public void ActivityEventsCoverActorStateIterationAndSortBoundaries()
    {
        var at = DateTimeOffset.UtcNow;
        var anonymousOrigin = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var namedOrigin = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("", "Named Actor"));
        var entries = new[]
        {
            new WorkerActionHistoryEntry(
                at,
                WorkerActionHistoryKind.WorkerAction,
                WorkAction.Start,
                WorkActionStatus.Accepted,
                anonymousOrigin,
                1,
                7,
                WorkerState.Running,
                [],
                1),
            new WorkerActionHistoryEntry(
                at,
                WorkerActionHistoryKind.WorkerAction,
                null,
                WorkActionStatus.Accepted,
                namedOrigin,
                2,
                6,
                WorkerState.Waiting,
                [],
                null),
        };
        var completed = CreateIteration(
            sequence: 2,
            status: WorkCompletionStatus.Completed,
            startedAt: at,
            completedAt: at,
            duration: default,
            messages: [],
            logs: [],
            output: null,
            attemptCount: 0);
        var worker = CreateWorkerSnapshot(
            state: WorkerState.Running,
            stateSequence: 7,
            actionHistory: entries);

        var events = worker.GetActivityEvents([completed]);

        Assert.Equal(3, events.Count);
        Assert.Equal(WorkerActivityEventKind.Iteration, events[0].Kind);
        Assert.Contains(events, item =>
            item.Kind == WorkerActivityEventKind.ActionRequest &&
            item.Category == WorkerActivityEventCategory.SystemEvent &&
            item.Action == WorkAction.Start);
        Assert.Contains(events, item =>
            item.Kind == WorkerActivityEventKind.ActionRequest &&
            item.Category == WorkerActivityEventCategory.UserAction &&
            item.Action is null &&
            item.Id.Contains(":none:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.Kind == WorkerActivityEventKind.StateChange);
        Assert.Equal(WorkerActivityEventCategory.SystemEvent, events[0].Category);
        Assert.Equal(at, events[0].At);
    }

    [Fact]
    public void FailureResolverUsesRetainedLogsAndJsonMetadataFallbacks()
    {
        var logFailure = WorkerIterationFailureResolver.Resolve(
            messages: [],
            logs:
            [
                new WorkerLogEntry(
                    DateTimeOffset.UtcNow,
                    WorkerId.New(),
                    WorkDefinitionId.New(),
                    "tests.failure",
                    LogLevel.Error,
                    new EventId(10, "failure"),
                    "rendered failure",
                    "System.TimeoutException",
                    "The operation timed out."),
            ],
            fallbackMessage: "fallback");
        var metadataFailure = WorkerIterationFailureResolver.Resolve(
            messages:
            [
                new WorkMessage(
                    "metadata.failure",
                    WorkMessageSeverity.Error,
                    "metadata fallback",
                    Metadata: new Dictionary<string, object?>
                    {
                        ["failureSource"] = JsonSerializer.SerializeToElement("executionContext"),
                        ["exceptionType"] = JsonSerializer.SerializeToElement("System.InvalidOperationException"),
                        ["exceptionMessage"] = JsonSerializer.SerializeToElement(42),
                    }),
            ],
            logs: [],
            fallbackMessage: "fallback");

        Assert.Equal(WorkerIterationFailureKind.Exception, logFailure.Kind);
        Assert.Equal("System.TimeoutException", logFailure.ExceptionType);
        Assert.Equal("The operation timed out.", logFailure.Message);
        Assert.Equal(WorkerIterationFailureKind.Exception, metadataFailure.Kind);
        Assert.Equal("42", metadataFailure.Message);
        Assert.True(metadataFailure.DeclaredByWork);
    }

    [Fact]
    public void FailureResolverCoversBlankExceptionAndPartialLogFallbacks()
    {
        var blankExceptionMessage = WorkerIterationFailureResolver.Resolve(
            messages:
            [
                new WorkMessage(
                    "blank.exception",
                    WorkMessageSeverity.Error,
                    "declared text",
                    Metadata: new Dictionary<string, object?>
                    {
                        ["exceptionType"] = "System.InvalidOperationException",
                        ["exceptionMessage"] = " ",
                    }),
            ],
            logs: null,
            fallbackMessage: "fallback");
        var messageOnlyLog = WorkerIterationFailureResolver.Resolve(
            messages: null,
            logs:
            [
                new WorkerLogEntry(
                    DateTimeOffset.UtcNow,
                    WorkerId.New(),
                    WorkDefinitionId.New(),
                    "tests.failure",
                    LogLevel.Critical,
                    new EventId(11, "critical"),
                    "critical failure",
                    ExceptionType: null,
                    ExceptionMessage: "message only"),
            ],
            fallbackMessage: "fallback");
        var typeOnlyLog = WorkerIterationFailureResolver.Resolve(
            messages: [],
            logs:
            [
                new WorkerLogEntry(
                    DateTimeOffset.UtcNow,
                    WorkerId.New(),
                    WorkDefinitionId.New(),
                    "tests.failure",
                    LogLevel.Error,
                    new EventId(12, "error"),
                    "typed failure",
                    ExceptionType: "System.Exception",
                    ExceptionMessage: null),
            ],
            fallbackMessage: "fallback");
        var declaredFailure = WorkerIterationFailureResolver.Resolve(
            messages: [WorkMessage.Error("declared", "declared failure")],
            logs: [],
            fallbackMessage: "fallback");
        var fallbackFailure = WorkerIterationFailureResolver.Resolve(
            messages: [WorkMessage.Error("blank", " ")],
            logs: [],
            fallbackMessage: "fallback");

        Assert.Equal("The execution failed because an exception was raised.", blankExceptionMessage.Message);
        Assert.Equal(WorkerIterationFailureKind.Failure, messageOnlyLog.Kind);
        Assert.Equal("message only", messageOnlyLog.Message);
        Assert.Equal(WorkerIterationFailureKind.Exception, typeOnlyLog.Kind);
        Assert.Equal("fallback", typeOnlyLog.Message);
        Assert.Equal("declared failure", declaredFailure.Message);
        Assert.Equal("fallback", fallbackFailure.Message);
    }

    [Fact]
    public void WorkerOverviewProjectionPreservesOperationalSummaryFields()
    {
        var now = DateTimeOffset.UtcNow;
        var identifier = new WorkIdentifier("invoice", "INV-42");
        var summary = new WorkerSummary(
            WorkerId.New(),
            7,
            5,
            "projection.worker",
            "Projection:Workers",
            new WorkSubjectId("account", "ACCT-1"),
            new WorkConcurrencyKey("tenant", "TENANT-1"),
            new HashSet<WorkIdentifier> { identifier },
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi),
            WorkerState.Waiting,
            null,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            now)
        {
            QueueDuration = TimeSpan.FromSeconds(3),
            TotalExecutionDuration = TimeSpan.FromSeconds(8),
            NextRunAt = now.AddMinutes(5),
        };

        var overview = WorkerOverviewItem.From(summary);

        Assert.Equal(summary.Id, overview.Id);
        Assert.Equal(summary.DefinitionName, overview.DefinitionName);
        Assert.Equal(summary.DefinitionCategory, overview.Category);
        Assert.Equal(summary.SubjectId, overview.SubjectId);
        Assert.Equal(summary.ConcurrencyKey, overview.ConcurrencyKey);
        Assert.Contains(identifier, overview.Identifiers);
        Assert.Equal(summary.QueueDuration, overview.QueueDuration);
        Assert.Equal(summary.TotalExecutionDuration, overview.TotalExecutionDuration);
        Assert.Equal(summary.NextRunAt, overview.NextRunAt);
        Assert.False(overview.IsFinal);
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
            Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
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
            1,
            stateSequence,
            "tests.worker.snapshot",
            "Tests",
            null,
            null,
            new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            state,
            null,
            null,
            WorkerOptions.Default,
            WorkConfiguration.Default,
            [],
            null,
            now.AddMinutes(-5),
            now.AddMinutes(-1),
            now)
        {
            ActionHistory = actionHistory ?? [],
            CurrentIteration = currentIteration,
            CurrentIterationSequence = currentIteration?.Sequence,
            Iterations = iterations ?? [],
            LastIteration = iterations?.OrderByDescending(iteration => iteration.Sequence).FirstOrDefault(),
            LastIterationSequence = iterations?.OrderByDescending(iteration => iteration.Sequence).FirstOrDefault()?.Sequence,
        };
    }

    private static WorkerIterationSnapshot CreateIteration(
        long sequence,
        WorkCompletionStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        TimeSpan duration,
        IReadOnlyList<WorkMessage>? messages,
        IReadOnlyList<WorkerLogEntry>? logs,
        WorkOutput? output,
        int attemptCount)
        => new(
            sequence,
            startedAt,
            completedAt,
            duration,
            status,
            attemptCount,
            output,
            messages!)
        {
            Logs = logs!,
        };
}
