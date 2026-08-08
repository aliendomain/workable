using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableSignalRTests
{
    private static readonly TimeSpan ManualViewPublishInterval = TimeSpan.FromMinutes(7);
    private static readonly TimeSpan ManualDiagnosticsPublishInterval = TimeSpan.FromMinutes(11);

    [Fact]
    public async Task HostEndpointReportsRealtimeDisabledWhenSignalRIsNotRegistered()
    {
        using var host = await CreateHost(addSignalR: false);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);

        Assert.False(response.Capabilities.Realtime.Enabled);
        Assert.Null(response.Capabilities.Realtime.Transport);
        Assert.Null(response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointReportsRealtimeEnabledWhenSignalRIsRegistered()
    {
        using var host = await CreateHost(addSignalR: true);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);
        var capabilities = response.Capabilities;

        Assert.True(capabilities.Realtime.Enabled);
        Assert.Equal("signalr", capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointReportsRealtimeForAuthenticatedCallerWithoutSystemAccess()
    {
        using var host = await CreateHost(
            addSignalR: true,
            groups: Array.Empty<string>());
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);

        Assert.True(response.Capabilities.Realtime.Enabled);
        Assert.Equal("signalr", response.Capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", response.Capabilities.Realtime.HubPath);
        Assert.Empty(response.Systems);
    }

    [Fact]
    public async Task HostEndpointUsesMappedRealtimeHubPath()
    {
        using var host = await CreateHost(addSignalR: true, hubPath: "/custom/realtime");
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);

        Assert.Equal("/custom/realtime", response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointIncludesRealtimeCapabilities()
    {
        using var host = await CreateHost(addSignalR: true);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());

        Assert.NotNull(response);
        var system = Assert.Single(response.Systems);
        Assert.True(system.IsDefault);
        Assert.True(response.Capabilities.Realtime.Enabled);
        Assert.Equal("signalr", response.Capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task SignalREventStreamSubscribesOnlyWhileEventWatchersAreActive()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var stream = GetEventStream(system);
        await using var connection = CreateConnection(host);

        await connection.StartAsync();

        Assert.Equal(0, stream.ActiveSubscriptionCount);
        await connection.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
        await TestEventually.Until(() => stream.ActiveSubscriptionCount == 1);

        await connection.InvokeAsync("UnwatchEvents", new WorkableRealtimeEventCriteria(), null);

        await TestEventually.Until(() => stream.ActiveSubscriptionCount == 0);
    }

    [Fact]
    public async Task EventWatcherReceivesOnlySelectedEventTypes()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var eventSubscriptions = host.Services.GetRequiredService<WorkableRealtimeEventSubscriptions>();
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handle = await session.Queue.Enqueue(definition.Name);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await handle.WaitForCompletion();

        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "worker.completed");
        var debugSubscription = Assert.Single(eventSubscriptions.GetDebugSubscriptions(system));
        var filter = debugSubscription.Filter ?? throw new InvalidOperationException("Expected filtered event subscription.");

        Assert.Equal(handle.WorkerId, completed.WorkerId);
        Assert.Equal("worker.completed", completed.EventType);
        Assert.Equal(definition.Name, completed.WorkDefinitionName);
        Assert.Equal(["worker.completed"], Required(filter.EventTypes).ToArray());
    }

    [Fact]
    public async Task EventWatcherReceivesAllEventsWhenCriteriaIsEmpty()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handle = await session.Queue.Enqueue(definition.Name);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await handle.WaitForCompletion();

        var queued = await ReadUntil(
            events.Reader,
            workEvent => workEvent.WorkerId == handle.WorkerId && workEvent.EventType == "worker.queued");
        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.WorkerId == handle.WorkerId && workEvent.EventType == "worker.completed");

        Assert.Equal(handle.WorkerId, queued.WorkerId);
        Assert.Equal("worker.queued", queued.EventType);
        Assert.Equal(definition.Name, queued.WorkDefinitionName);
        Assert.Equal(handle.WorkerId, completed.WorkerId);
        Assert.Equal("worker.completed", completed.EventType);
        Assert.Equal(definition.Name, completed.WorkDefinitionName);
    }

    [Fact]
    public async Task IterationStatusStreamReplaysInOrderAndResumesAfterASequence()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("signalr.iteration-status"),
                (context, _, _) =>
                {
                    context.Status.Publish("assistant.text.delta", "one");
                    context.Status.Publish("assistant.text.delta", "two");
                    context.Status.Publish("assistant.text.delta", "three");
                    return Task.FromResult(WorkExecutionResult.Success());
                }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var handle = await Session(system).Queue.Enqueue("signalr.iteration-status");
        var completion = await handle.WaitForCompletion();
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected a completed worker.");
        var iterationSequence = worker.LastIterationSequence
            ?? throw new InvalidOperationException("Expected a completed iteration.");
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var stream = connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamIterationStatus",
            [worker.Id.Value.ToString("D"), iterationSequence, 1L, null],
            CancellationToken.None);
        var messages = await ReadStream(stream);
        var statusMessages = messages
            .Where(message => message.Kind == WorkableRealtimeIterationStatusMessage.StatusKind)
            .ToArray();
        var items = statusMessages
            .Select(message => Assert.IsType<WorkableRealtimeIterationStatus>(message.Status))
            .ToArray();
        var completed = Assert.IsType<WorkableRealtimeIterationCompleted>(
            Assert.Single(messages, message =>
                message.Kind == WorkableRealtimeIterationStatusMessage.CompletedKind).Completed);

        Assert.Equal([2L, 3L], items.Select(static item => item.Sequence));
        Assert.Equal(["two", "three"], items.Select(static item => item.Data?.GetString()));
        Assert.All(items, item =>
        {
            Assert.Equal(worker.Id, item.WorkerId);
            Assert.Equal(iterationSequence, item.IterationSequence);
            Assert.Equal("signalr.iteration-status", item.WorkDefinitionName);
            Assert.Equal("assistant.text.delta", item.Type);
        });
        Assert.All(statusMessages, message =>
        {
            Assert.Equal(WorkableRealtimeIterationStatusMessage.StatusKind, message.Kind);
            Assert.Null(message.Gap);
            Assert.Null(message.Completed);
        });
        Assert.Equal(worker.Id, completed.WorkerId);
        Assert.Equal(iterationSequence, completed.IterationSequence);
        Assert.Equal(WorkCompletionStatus.Completed, completed.Status);
        Assert.Null(completed.CancellationOrigin);
    }

    [Fact]
    public async Task MyIterationStatusStreamReturnsGenericTerminalOutputOnlyForOriginatingActor()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("signalr.my-iteration-status"),
                (context, _, _) =>
                {
                    context.Status.Publish("assistant.text.delta", "hello");
                    return Task.FromResult(WorkExecutionResult.Success(
                        WorkOutput.FromValue(new { reply = "done" })));
                }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var currentSession = TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            actor: new WorkActor("signalr-user-1", "SignalR User"));
        var otherSession = TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            actor: new WorkActor("signalr-other-user", "Other User"));
        var ownHandle = await currentSession.Queue.Enqueue("signalr.my-iteration-status");
        var otherHandle = await otherSession.Queue.Enqueue("signalr.my-iteration-status");
        var ownWorker = (await ownHandle.WaitForCompletion()).Worker
            ?? throw new InvalidOperationException("Expected the actor's worker.");
        var otherWorker = (await otherHandle.WaitForCompletion()).Worker
            ?? throw new InvalidOperationException("Expected the other actor's worker.");
        var ownIteration = ownWorker.LastIterationSequence
            ?? throw new InvalidOperationException("Expected the actor's iteration.");
        var otherIteration = otherWorker.LastIterationSequence
            ?? throw new InvalidOperationException("Expected the other actor's iteration.");
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var ownMessages = await ReadStream(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamMyIterationStatus",
            [ownWorker.Id.Value.ToString("D"), ownIteration, 0L, null],
            CancellationToken.None));
        var completed = Assert.IsType<WorkableRealtimeIterationCompleted>(
            Assert.Single(ownMessages, message =>
                message.Kind == WorkableRealtimeIterationStatusMessage.CompletedKind).Completed);
        var output = completed.Output?.ToValue<JsonElement>()
            ?? throw new InvalidOperationException("Expected generic terminal output.");

        Assert.Single(ownMessages, message =>
            message.Kind == WorkableRealtimeIterationStatusMessage.StatusKind);
        Assert.Equal(ownWorker.Id, completed.WorkerId);
        Assert.Equal(ownIteration, completed.IterationSequence);
        Assert.Equal(WorkCompletionStatus.Completed, completed.Status);
        Assert.Equal("done", output.GetProperty("reply").GetString());

        var otherMessages = await ReadStream(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamMyIterationStatus",
            [otherWorker.Id.Value.ToString("D"), otherIteration, 0L, null],
            CancellationToken.None));

        Assert.Empty(otherMessages);
    }

    [Fact]
    public async Task MyIterationStatusStreamIncludesAcceptedCancellationOrigin()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        var actor = new WorkActor("signalr-user-1", "SignalR User");
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, actor: actor);
        var handle = await session.Queue.Enqueue("signalr.view");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected a worker id.");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = await session.Query.Worker(workerId)
                ?? throw new InvalidOperationException("Expected the executing worker.");
            var iterationSequence = worker.CurrentIterationSequence
                ?? throw new InvalidOperationException("Expected the current iteration.");
            await using var connection = CreateConnection(host);
            await connection.StartAsync();
            var read = ReadStream(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
                "StreamMyIterationStatus",
                [workerId.Value.ToString("D"), iterationSequence, 0L, null],
                CancellationToken.None));

            var cancellation = await session.Workers.Execute(worker.Version, WorkAction.Cancel);
            Assert.True(cancellation.IsAccepted);
            var messages = await read.WaitAsync(TimeSpan.FromSeconds(5));
            var completed = Assert.IsType<WorkableRealtimeIterationCompleted>(
                Assert.Single(messages, message =>
                    message.Kind == WorkableRealtimeIterationStatusMessage.CompletedKind).Completed);

            Assert.Equal(WorkCompletionStatus.Canceled, completed.Status);
            Assert.Equal(actor.Id, completed.CancellationOrigin?.Actor.Id);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task IterationStatusStreamRejectsInvalidArgumentsAndFutureCursors()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("signalr.iteration-status.validation"),
                (context, _, _) =>
                {
                    context.Status.Publish("progress", 1);
                    return Task.FromResult(WorkExecutionResult.Success());
                }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var completion = await (await Session(system).Queue.Enqueue("signalr.iteration-status.validation"))
            .WaitForCompletion();
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected a completed worker.");
        var iterationSequence = worker.LastIterationSequence
            ?? throw new InvalidOperationException("Expected a completed iteration.");
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var invalidIteration = await ReadStreamFailure(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamIterationStatus",
            [worker.Id.Value.ToString("D"), 0L, 0L, null],
            CancellationToken.None));
        var negativeCursor = await ReadStreamFailure(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamIterationStatus",
            [worker.Id.Value.ToString("D"), iterationSequence, -1L, null],
            CancellationToken.None));
        var futureCursor = await ReadStreamFailure(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamIterationStatus",
            [worker.Id.Value.ToString("D"), iterationSequence, 2L, null],
            CancellationToken.None));

        Assert.Contains("iteration sequence must be greater than zero", invalidIteration.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cursor cannot be negative", negativeCursor.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last published sequence 1", futureCursor.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IterationStatusStreamDoesNotRevealUnknownIterations()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var stream = connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamIterationStatus",
            [WorkerId.New().Value.ToString("D"), 1L, 0L, null],
            CancellationToken.None);
        var items = await ReadStream(stream);

        Assert.Empty(items);
    }

    [Fact]
    public async Task IterationStatusStreamReportsAnExpiredReplayCursor()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder
                .ConfigureIterationStatuses(
                    replayItemCapacity: 10,
                    replayPayloadByteCapacity: 24,
                    maximumPayloadBytes: 8,
                    maximumTypeBytes: 8)
                .AddAuthorizedTransportWork(
                    WorkDefinition.Create("signalr.iteration-status.gap"),
                    (context, _, _) =>
                    {
                        for (var index = 1; index <= 3; index++)
                        {
                            context.Status.Publish("progress", index switch
                            {
                                1 => "aa",
                                2 => "bb",
                                _ => "cc",
                            });
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var completion = await (await Session(system).Queue.Enqueue("signalr.iteration-status.gap"))
            .WaitForCompletion();
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected a completed worker.");
        var iterationSequence = worker.LastIterationSequence
            ?? throw new InvalidOperationException("Expected a completed iteration.");
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var messages = await ReadStream(connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
            "StreamIterationStatus",
            [worker.Id.Value.ToString("D"), iterationSequence, 0L, null],
            CancellationToken.None));
        var message = Assert.Single(messages);
        var gap = Assert.IsType<WorkableRealtimeIterationStatusGap>(message.Gap);

        Assert.Equal(WorkableRealtimeIterationStatusMessage.GapKind, message.Kind);
        Assert.Null(message.Status);
        Assert.Equal(worker.Id, gap.WorkerId);
        Assert.Equal(iterationSequence, gap.IterationSequence);
        Assert.Equal(0, gap.RequestedAfterSequence);
        Assert.Equal(2, gap.FirstAvailableSequence);
        Assert.Equal(3, gap.LastAvailableSequence);
    }

    [Fact]
    public async Task IterationStatusSignalRReaderMapsALiveReplayGapAndDisposesTheSubscription()
    {
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        var subscription = new GapIterationStatusSubscription(
            new WorkIterationStatusGapException(
                iteration,
                afterSequence: 4,
                firstAvailableSequence: 7,
                lastAvailableSequence: 9));

        var messages = await ReadStream(WorkableRealtimeHub.ReadIterationStatus(
            subscription,
            CancellationToken.None,
            CancellationToken.None));
        var message = Assert.Single(messages);
        var gap = Assert.IsType<WorkableRealtimeIterationStatusGap>(message.Gap);

        Assert.Equal(WorkableRealtimeIterationStatusMessage.GapKind, message.Kind);
        Assert.Equal(4, gap.RequestedAfterSequence);
        Assert.Equal(7, gap.FirstAvailableSequence);
        Assert.Equal(9, gap.LastAvailableSequence);
        Assert.True(subscription.IsDisposed);
    }

    [Fact]
    public void IterationStatusSignalRSubscriptionMapsAMissingRawStreamToAClientSafeError()
    {
        var iteration = new WorkerIterationReference(WorkerId.New(), 3);

        var exception = Assert.Throws<Microsoft.AspNetCore.SignalR.HubException>(() =>
            WorkableRealtimeHub.SubscribeIterationStatus(
                MissingIterationStatusStream.Instance,
                iteration,
                afterSequence: 0));

        Assert.Contains(iteration.WorkerId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("iteration 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("available status stream", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IterationStatusSignalRSubscriptionMapsASubscriptionLimitToAClientSafeError()
    {
        var iteration = new WorkerIterationReference(WorkerId.New(), 3);
        var stream = new LimitedIterationStatusStream(iteration);

        var exception = Assert.Throws<Microsoft.AspNetCore.SignalR.HubException>(() =>
            WorkableRealtimeHub.SubscribeIterationStatus(stream, iteration, afterSequence: 0));

        Assert.Contains("limit of 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active status subscriptions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EventWatcherFiltersByDefinitionAndKey()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var eventSubscriptions = host.Services.GetRequiredService<WorkableRealtimeEventSubscriptions>();
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        var definition = Session(system).Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var acceptedIdentifier = new WorkIdentifier("batch", "accepted");
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(
                EventTypes: ["worker.completed"],
                DefinitionNames: [definition.Name],
                Keys:
                [
                    new WorkableRealtimeEventKeyCriteria(
                        WorkKeyKind.Identifier,
                        acceptedIdentifier.Type,
                        acceptedIdentifier.Value),
                ]),
            null);

        var session = Session(system);
        var accepted = await session.Queue.Enqueue("signalr.view", WorkInput.Empty.WithIdentifier(acceptedIdentifier));
        var ignored = await session.Queue.Enqueue("signalr.view", WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "ignored")));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await Task.WhenAll(accepted.WaitForCompletion(), ignored.WaitForCompletion());

        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "worker.completed");
        var debugSubscription = Assert.Single(eventSubscriptions.GetDebugSubscriptions(system));
        var filter = debugSubscription.Filter ?? throw new InvalidOperationException("Expected filtered event subscription.");
        var key = Assert.Single(Required(filter.Keys));

        Assert.Equal(accepted.WorkerId, completed.WorkerId);
        Assert.Equal("worker.completed", completed.EventType);
        Assert.Equal(definition.Name, completed.WorkDefinitionName);
        Assert.Equal([acceptedIdentifier], completed.Identifiers.ToArray());
        Assert.Equal([definition.Name], Required(filter.DefinitionNames).ToArray());
        Assert.Equal(WorkKeyKind.Identifier, key.Kind);
        Assert.Equal(acceptedIdentifier.Type, key.Type);
        Assert.Equal(acceptedIdentifier.Value, key.Value);
    }

    [Fact]
    public async Task EventWatcherCanReceiveWorkflowEventsFilteredByDefinitionAndRunKey()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder =>
            {
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("signalr.workflow.child"),
                    async (_, _, cancellationToken) =>
                    {
                        childStarted.TrySetResult();
                        await releaseChild.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("signalr.workflow"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("signalr.workflow.child")),
                    authorize => authorize
                        .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                        .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);

        var workflowHandle = Assert.IsType<InMemoryWorkSystem>(system).WorkflowRuntime.Start(
            "signalr.workflow",
            TransportAuthorizationTestSupport.CreateTransportRequestContext(
                WorkInvocationChannel.InProcess,
                description: "Start workflow for SignalR workflow event test."));
        var runId = workflowHandle.RunId?.Value.ToString("D")
            ?? throw new InvalidOperationException("Expected workflow run id.");
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(
                EventTypes: ["workflow.completed"],
                DefinitionNames: ["signalr.workflow"],
                Keys:
                [
                    new WorkableRealtimeEventKeyCriteria(
                        WorkKeyKind.Identifier,
                        "workflow-run",
                        runId),
                ]),
            null);

        releaseChild.TrySetResult();
        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "workflow.completed");
        var data = completed.Data ?? throw new InvalidOperationException("Expected workflow event payload.");

        Assert.Equal("signalr.workflow", completed.WorkDefinitionName);
        Assert.Contains(completed.Identifiers, identifier => identifier.Type == "workflow-run" && identifier.Value == runId);
        Assert.Equal("signalr.workflow", data.GetProperty("run").GetProperty("definitionName").GetString());
        Assert.Equal("Completed", data.GetProperty("run").GetProperty("status").GetString());
    }

    [Fact]
    public async Task EventWatcherReceivesBurstsAsBatches()
    {
        using var host = await CreateHost(addSignalR: true, configureSignalR: options =>
        {
            options.BatchTimeWindow = TimeSpan.FromMilliseconds(500);
            options.EventMaxBatchSize = 10;
        });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var batches = Channel.CreateUnbounded<WorkableRealtimeEventBatch>();
        connection.On<WorkableRealtimeEventBatch>(
            WorkableRealtimeClientMethods.WorkEvents,
            batch => batches.Writer.TryWrite(batch));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handles = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => session.Queue.Enqueue(definition.Name)));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await Task.WhenAll(handles.Select(handle => handle.WaitForCompletion()));
        var expectedWorkerIds = handles
            .Select(handle => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker."))
            .ToHashSet();

        var batch = await ReadUntil(
            batches.Reader,
            batch => batch.Events.Count == expectedWorkerIds.Count &&
                batch.Events.Select(workEvent => workEvent.WorkerId).OfType<WorkerId>().ToHashSet().SetEquals(expectedWorkerIds));
        var actualWorkerIds = batch.Events
            .Select(workEvent => workEvent.WorkerId)
            .OfType<WorkerId>()
            .ToHashSet();

        Assert.Equal(3, batch.Events.Count);
        Assert.All(batch.Events, workEvent =>
        {
            Assert.Equal("worker.completed", workEvent.EventType);
            Assert.Equal(definition.Name, workEvent.WorkDefinitionName);
        });
        Assert.Equal(
            expectedWorkerIds.OrderBy(static workerId => workerId.Value).ToArray(),
            actualWorkerIds.OrderBy(static workerId => workerId.Value).ToArray());
    }

    [Fact]
    public async Task EventWatcherFlushesFullBatchWithoutWaitingForBatchWindow()
    {
        using var host = await CreateHost(addSignalR: true, configureSignalR: options =>
        {
            options.BatchTimeWindow = TimeSpan.FromSeconds(5);
            options.EventMaxBatchSize = 2;
        });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var batches = Channel.CreateUnbounded<WorkableRealtimeEventBatch>();
        connection.On<WorkableRealtimeEventBatch>(
            WorkableRealtimeClientMethods.WorkEvents,
            batch => batches.Writer.TryWrite(batch));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handles = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => session.Queue.Enqueue(definition.Name)));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await Task.WhenAll(handles.Select(handle => handle.WaitForCompletion()));
        var expectedWorkerIds = handles
            .Select(handle => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker."))
            .ToHashSet();

        var batch = await ReadUntil(
                batches.Reader,
                batch => batch.Events.Count == expectedWorkerIds.Count &&
                    batch.Events.Select(workEvent => workEvent.WorkerId).OfType<WorkerId>().ToHashSet().SetEquals(expectedWorkerIds))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, batch.Events.Count);
        Assert.All(batch.Events, workEvent => Assert.Equal("worker.completed", workEvent.EventType));
    }

    [Fact]
    public async Task EventWatcherRejectsUnknownSystemNames()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(),
            "missing-system"));

        Assert.Contains("missing-system", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewWatcherReceivesRequestedOverviewComponentsOnly()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "overview";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workers"));
        var initialWorkers = Assert.IsType<JsonElement>(initial.Components["workers"].Data);

        var handle = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        var updated = await ReadUntil(
            views.Reader,
            view =>
            {
                if (view.GeneratedAt <= initial.GeneratedAt ||
                    !view.Components.TryGetValue("workers", out var component) ||
                    component.Data is not JsonElement workerData)
                {
                    return false;
                }

                return workerData.GetProperty("activeWorkerCount").GetInt32() == 0 &&
                    workerData.GetProperty("failedWorkerCount").GetInt32() == 0;
            });
        var workers = Assert.IsType<JsonElement>(updated.Components["workers"].Data);

        Assert.Equal(["system", "workers"], initial.Components.Keys.Order().ToArray());
        Assert.Equal(["system", "workers"], updated.Components.Keys.Order().ToArray());
        Assert.Equal("compact", initial.Components["workers"].Shape);
        Assert.Equal("compact", updated.Components["workers"].Shape);
        Assert.Equal(0, initialWorkers.GetProperty("activeWorkerCount").GetInt32());
        Assert.Equal(0, initialWorkers.GetProperty("failedWorkerCount").GetInt32());
        Assert.False(initialWorkers.TryGetProperty("finalWorkerCount", out _));
        Assert.Equal(0, workers.GetProperty("activeWorkerCount").GetInt32());
        Assert.Equal(0, workers.GetProperty("failedWorkerCount").GetInt32());
        Assert.False(workers.TryGetProperty("finalWorkerCount", out _));
    }

    [Fact]
    public async Task ViewWatcherReceivesStateChangesWithoutPublishIntervalTick()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "overview";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workers"));

        var handle = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        var updated = await ReadUntil(
            views.Reader,
            view =>
            {
                if (view.GeneratedAt <= initial.GeneratedAt ||
                    !view.Components.TryGetValue("workers", out var component) ||
                    component.Data is not JsonElement workerData)
                {
                    return false;
                }

                return workerData.GetProperty("activeWorkerCount").GetInt32() == 0 &&
                    workerData.GetProperty("failedWorkerCount").GetInt32() == 0;
            });
        var workers = Assert.IsType<JsonElement>(updated.Components["workers"].Data);

        Assert.Equal(["workers"], updated.Components.Keys.ToArray());
        Assert.Equal(0, workers.GetProperty("activeWorkerCount").GetInt32());
        Assert.Equal(0, workers.GetProperty("failedWorkerCount").GetInt32());
    }

    [Fact]
    public async Task WorkersWatcherScopesSnapshotsAndChangesToOriginActor()
    {
        using var host = await CreateHost(
            addSignalR: true,
            groups: TransportAuthorizationTestSupport.ReadGroups
                .Concat(TransportAuthorizationTestSupport.OperateGroups));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var targetActor = new WorkActor("signalr-origin-user", "Origin User");
        var otherActor = new WorkActor("signalr-other-user", "Other User");
        var targetSession = TransportAuthorizationTestSupport.CreateTransportSession(system, actor: targetActor);
        var otherSession = TransportAuthorizationTestSupport.CreateTransportSession(system, actor: otherActor);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "actor-workers";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchWorkers",
            subscriptionId,
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "workerGrid",
                    "workerGrid",
                    JsonSerializer.SerializeToElement(new { actorId = targetActor.Id }),
                    WorkComponentShapes.Detailed),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workerGrid"));
        var initialGrid = Assert.IsType<JsonElement>(initial.Components["workerGrid"].Data);
        Assert.Equal(0, initialGrid.GetProperty("totalCount").GetInt32());
        Assert.Empty(initialGrid.GetProperty("workers").EnumerateArray());

        var other = await otherSession.Queue.Enqueue("signalr.worker");
        var otherWorkerId = other.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        await TestEventually.Until(async () => await otherSession.Query.Worker(otherWorkerId) is not null);
        await AssertNoItem(views.Reader, TimeSpan.FromMilliseconds(500));

        var target = await targetSession.Queue.Enqueue("signalr.worker");
        var updated = await ReadUntil(
            views.Reader,
            view =>
            {
                if (!view.Components.TryGetValue("workerGrid", out var component) ||
                    component.Data is not JsonElement data)
                {
                    return false;
                }

                return data.GetProperty("workers")
                    .EnumerateArray()
                    .Any(worker => worker.GetProperty("id").GetProperty("value").GetGuid() == target.WorkerId?.Value);
            });
        var updatedGrid = Assert.IsType<JsonElement>(updated.Components["workerGrid"].Data);

        Assert.Equal(1, updatedGrid.GetProperty("totalCount").GetInt32());
        Assert.Single(updatedGrid.GetProperty("workers").EnumerateArray());
    }

    [Fact]
    public async Task MyWorkersWatcherUsesAuthenticatedActorAndIgnoresSpoofedActorOptions()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var currentActor = new WorkActor("signalr-user-1", "SignalR User");
        var otherActor = new WorkActor("signalr-other-user", "Other User");
        var currentSession = TransportAuthorizationTestSupport.CreateTransportSession(system, actor: currentActor);
        var otherSession = TransportAuthorizationTestSupport.CreateTransportSession(system, actor: otherActor);
        var current = await currentSession.Queue.Enqueue("signalr.worker");
        var other = await otherSession.Queue.Enqueue("signalr.worker");
        await using var connection = CreateConnection(host);
        const string subscriptionId = "my-workers";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();

        await connection.InvokeAsync(
            "WatchMyWorkers",
            subscriptionId,
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "workerGrid",
                    "workerGrid",
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["actorId"] = otherActor.Id,
                        ["ACTORID"] = "another-spoofed-actor",
                        ["take"] = 5,
                    }),
                    WorkComponentShapes.Detailed),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workerGrid"));
        var grid = Assert.IsType<JsonElement>(initial.Components["workerGrid"].Data);
        var worker = Assert.Single(grid.GetProperty("workers").EnumerateArray());

        Assert.Equal(1, grid.GetProperty("totalCount").GetInt32());
        Assert.Equal(current.WorkerId?.Value, worker.GetProperty("id").GetProperty("value").GetGuid());
        Assert.NotEqual(other.WorkerId?.Value, worker.GetProperty("id").GetProperty("value").GetGuid());
    }

    [Fact]
    public async Task MyWorkersWatcherPublishesCurrentIterationSequenceWithoutIterationPayloads()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        var currentSession = TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            actor: new WorkActor("signalr-user-1", "SignalR User"));
        await using var connection = CreateConnection(host);
        const string subscriptionId = "my-running-workers";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync("WatchMyWorkers", subscriptionId, null, null);
        _ = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workerGrid"));

        var handle = await currentSession.Queue.Enqueue("signalr.view");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected a worker id.");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = await ReadUntil(
                views.Reader,
                view => TryFindWorker(view, workerId, out var row) &&
                    row.GetProperty("currentIterationSequence").GetInt64() == 1);
            Assert.True(TryFindWorker(running, workerId, out var runningWorker));

            Assert.Equal(1, runningWorker.GetProperty("currentIterationSequence").GetInt64());
            Assert.False(runningWorker.TryGetProperty("iteration", out _));

            gate.Release.TrySetResult();
            _ = await handle.WaitForCompletion();
            var completed = await ReadUntil(
                views.Reader,
                view => TryFindWorker(view, workerId, out var row) &&
                    row.GetProperty("isFinal").GetBoolean());
            Assert.True(TryFindWorker(completed, workerId, out var completedWorker));
            Assert.Equal(JsonValueKind.Null, completedWorker.GetProperty("currentIterationSequence").ValueKind);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task MyWorkersWatcherFailsClosedWithoutStableActorId()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "SignalR User Without Id")],
            "Test"));
        using var host = await CreateHost(addSignalR: true, principal: principal);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var subscriptions = host.Services.GetRequiredService<WorkableRealtimeViewSubscriptions>();
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            connection.InvokeAsync("WatchMyWorkers", "missing-actor", null, null));

        Assert.Contains("stable actor id", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
    }

    [Fact]
    public async Task WorkersWatcherUsesWorkersViewNameAndDefaultGrid()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "workers-default";
        var updates = Channel.CreateUnbounded<WorkableRealtimeViewEnvelope<WorkComponentQueryResult>>();
        connection.On<WorkableRealtimeViewEnvelope<WorkComponentQueryResult>>(
            WorkableRealtimeClientMethods.ViewUpdated,
            envelope => updates.Writer.TryWrite(envelope));
        await connection.StartAsync();

        await connection.InvokeAsync("WatchWorkers", subscriptionId, null, null);

        var initial = await ReadUntil(
            updates.Reader,
            envelope => string.Equals(envelope.SubscriptionId, subscriptionId, StringComparison.Ordinal));
        var workerGrid = Assert.IsType<JsonElement>(initial.Result.Components["workerGrid"].Data);

        Assert.Equal("workers", initial.ViewName);
        Assert.Equal(WorkComponentShapes.Detailed, initial.Result.Components["workerGrid"].Shape);
        Assert.Equal(0, workerGrid.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ActorScopedWorkersWatcherSeedsWorkCreatedWhileSubscriptionEstablishmentIsBlocked()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services =>
            {
                services.AddSingleton<BlockingViewGroupHubLifetimeManager>();
                services.AddSingleton<HubLifetimeManager<WorkableRealtimeHub>>(
                    provider => provider.GetRequiredService<BlockingViewGroupHubLifetimeManager>());
            });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var lifetime = host.Services.GetRequiredService<BlockingViewGroupHubLifetimeManager>();
        var subscriptions = host.Services.GetRequiredService<WorkableRealtimeViewSubscriptions>();
        await subscriptions.WaitForStreaming(system, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var targetActor = new WorkActor("signalr-racing-origin-user", "Racing Origin User");
        var targetSession = TransportAuthorizationTestSupport.CreateTransportSession(system, actor: targetActor);
        var existing = await targetSession.Queue.Enqueue("signalr.worker");
        var existingWorkerId = existing.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        await TestEventually.Until(async () => await targetSession.Query.Worker(existingWorkerId) is not null);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "actor-workers-race";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();

        var watch = connection.InvokeAsync(
            "WatchWorkers",
            subscriptionId,
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "workerGrid",
                    "workerGrid",
                    JsonSerializer.SerializeToElement(new { actorId = targetActor.Id }),
                    WorkComponentShapes.Detailed),
            ]),
            null);

        try
        {
            await lifetime.WaitForBlockedGroupAdd();
            var raced = await targetSession.Queue.Enqueue("signalr.worker");
            var racedWorkerId = raced.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
            await TestEventually.Until(async () => await targetSession.Query.Worker(racedWorkerId) is not null);

            // The actor-keyed change occurred after registration but before the initial query.
            // The direct seed must include it while broadcasts remain behind the seed barrier.
            lifetime.ReleaseGroupAdd();
            await watch;

            var initial = await ReadUntil(
                views.Reader,
                view =>
                {
                    if (!view.Components.TryGetValue("workerGrid", out var component) ||
                        component.Data is not JsonElement data)
                    {
                        return false;
                    }

                    var workerIds = data.GetProperty("workers")
                        .EnumerateArray()
                        .Select(worker => worker.GetProperty("id").GetProperty("value").GetGuid())
                        .ToHashSet();
                    return workerIds.Contains(existingWorkerId.Value) && workerIds.Contains(racedWorkerId.Value);
                });
            var initialGrid = Assert.IsType<JsonElement>(initial.Components["workerGrid"].Data);

            Assert.Equal(2, initialGrid.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, initialGrid.GetProperty("workers").GetArrayLength());
        }
        finally
        {
            lifetime.ReleaseGroupAdd();
        }
    }

    [Fact]
    public async Task ViewWatcherUsesChangeStreamWithoutEventStreamSubscription()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var eventStream = GetEventStream(system);
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "overview";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();

        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workers"));
        var handle = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        _ = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("workers"));

        Assert.Equal(0, eventStream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task ViewWatcherDoesNotReceiveWorkerViewForUnrelatedWorkerChanges()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        var session = Session(system);
        var target = await session.Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var targetCompletion = await target.WaitForCompletion();
        Assert.True(targetCompletion.IsCompletedSuccessfully);
        await TestEventually.Until(() => session.Diagnostics.ReadModel.PendingUpdateCount == 0);
        var targetWorkerId = target.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");

        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "worker",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "worker",
                    "workerDetail",
                    JsonSerializer.SerializeToElement(new
                    {
                        workerId = targetWorkerId.Value,
                    })),
            ]),
            null);

        _ = await ReadUntil(views.Reader, view => view.Components.ContainsKey("worker"));
        await DrainUntilQuiet(views.Reader, TimeSpan.FromMilliseconds(250));

        var unrelated = await session.Queue.Enqueue("signalr.view");
        var unrelatedCompletion = await unrelated.WaitForCompletion();
        Assert.True(unrelatedCompletion.IsCompletedSuccessfully);
        await TestEventually.Until(() => session.Diagnostics.ReadModel.PendingUpdateCount == 0);

        await AssertNoItem(views.Reader, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ViewWatcherContinuesPublishingOverviewThroughputWithoutReadModelChanges()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        await using var connection = CreateConnection(host);
        const string subscriptionId = "overview";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "throughput",
                    "throughput",
                    JsonSerializer.SerializeToElement(new { windowSeconds = 60, bucketSeconds = 1 }),
                    WorkComponentShapes.Standard),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("throughput"));
        await TestEventually.ClockAfter(initial.GeneratedAt);
        await timers.TickWhenReady(ManualViewPublishInterval);
        var updated = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("throughput"));

        Assert.Equal(["throughput"], updated.Components.Keys.ToArray());
    }

    [Fact]
    public async Task ViewWatcherReceivesWorkflowRunListUpdatesWhenWorkflowFailsWithoutReadModelChanges()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            },
            configureWorkable: builder =>
            {
                builder.UseCapacity(new WorkSystemCapacityConfiguration
                {
                    MaximumWorkers = 1,
                });
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("signalr.workflow.capacity-child"),
                    SuccessfulWork);
                builder.AddWorkflow(
                    WorkflowDefinition.Create("signalr.workflow.capacity"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("signalr.workflow.capacity-child")));
            });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "workflow-runs";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "workflow-runs",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "workflowRuns",
                    "workflowRuns",
                    JsonSerializer.SerializeToElement(new
                    {
                        includeFinal = true,
                    }),
                    WorkComponentShapes.Detailed),
            ]),
            null);

        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("workflowRuns"));
        var initialRuns = Assert.IsType<JsonElement>(initial.Components["workflowRuns"].Data);
        Assert.Equal(0, initialRuns.GetProperty("runs").GetArrayLength());

        var blocker = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var handle = Assert.IsType<InMemoryWorkSystem>(system).WorkflowRuntime.Start(
            "signalr.workflow.capacity",
            TransportAuthorizationTestSupport.CreateTransportRequestContext(
                WorkInvocationChannel.InProcess,
                description: "Start workflow that will fail before any worker is created."));
        await handle.WaitForCompletion();

        await TestEventually.ClockAfter(initial.GeneratedAt);
        await timers.TickWhenReady(ManualViewPublishInterval);
        using (var updateTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750)))
        {
            try
            {
                await foreach (var view in views.Reader.ReadAllAsync(updateTimeout.Token))
                {
                    if (view.GeneratedAt <= initial.GeneratedAt ||
                        !view.Components.TryGetValue("workflowRuns", out var component) ||
                        component.Data is not JsonElement data)
                    {
                        continue;
                    }

                    var runs = data.GetProperty("runs");
                    Assert.InRange(runs.GetArrayLength(), 0, 1);
                    if (runs.GetArrayLength() == 1)
                    {
                        Assert.Equal(WorkflowRunStatus.Failed.ToString(), runs[0].GetProperty("status").GetString());
                    }

                    break;
                }
            }
            catch (OperationCanceledException)
            {
                // A workflow that leaves no visible run can settle back to the same empty list without emitting a distinct update.
            }
        }

        gate.Release.TrySetResult();
        await blocker.WaitForCompletion();
    }

    [Fact]
    public async Task ViewWatcherReceivesWorkflowRunDetailUpdatesFromChildWorkerReadModelChanges()
    {
        var timers = new ManualRealtimeTimerFactory();
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            },
            configureWorkable: builder =>
            {
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create(
                        "signalr.workflow.manual-child",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    async (_, _, cancellationToken) =>
                    {
                        childStarted.TrySetResult();
                        await releaseChild.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("signalr.workflow.manual"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("signalr.workflow.manual-child")));
            });
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);
        var startContext = TransportAuthorizationTestSupport.CreateTransportRequestContext(
            WorkInvocationChannel.InProcess,
            description: "Start workflow for workflow detail realtime view test.");
        var handle = system.WorkflowRuntime.Start("signalr.workflow.manual", startContext);
        var runId = handle.RunId ?? throw new InvalidOperationException("Expected workflow run id.");
        await TestEventually.Until(() =>
        {
            var snapshot = system.WorkflowRuntime.Get(runId);
            return snapshot?.Steps.Single(step => step.Name == "dispatch").WorkerIds.Count == 1;
        });
        var childWorkerId = system.WorkflowRuntime.Get(runId)!
            .Steps
            .Single(step => step.Name == "dispatch")
            .WorkerIds
            .Single();

        await using var connection = CreateConnection(host);
        const string subscriptionId = "workflow-run";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "workflow-run",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "workflowRun",
                    "workflowRun",
                    JsonSerializer.SerializeToElement(new
                    {
                        runId = runId.Value.ToString("D"),
                    }),
                    WorkComponentShapes.Detailed),
            ]),
            null);

        var initial = await ReadUntil(
            views.Reader,
            view =>
            {
                if (!view.Components.TryGetValue("workflowRun", out var component) ||
                    component.Data is not JsonElement data)
                {
                    return false;
                }

                return data
                    .GetProperty("steps")[0]
                    .GetProperty("childSample")[0]
                    .GetProperty("state")
                    .GetString() == nameof(WorkerState.Queued);
            });
        var initialDetail = Assert.IsType<JsonElement>(initial.Components["workflowRun"].Data);
        Assert.Equal(nameof(WorkflowRunStatus.Running), initialDetail.GetProperty("status").GetString());

        var worker = await Session(system).Query.Worker(childWorkerId)
            ?? throw new InvalidOperationException("Expected child worker.");
        var start = await Session(system).Workers.Execute(worker.Version, WorkAction.Start);
        Assert.True(start.IsAccepted);
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await TestEventually.ClockAfter(initial.GeneratedAt);
        await timers.TickWhenReady(ManualViewPublishInterval);
        var started = await ReadUntil(
            views.Reader,
            view =>
            {
                if (view.GeneratedAt <= initial.GeneratedAt ||
                    !view.Components.TryGetValue("workflowRun", out var component) ||
                    component.Data is not JsonElement data)
                {
                    return false;
                }

                return data
                    .GetProperty("steps")[0]
                    .GetProperty("childSample")[0]
                    .GetProperty("state")
                    .GetString() == nameof(WorkerState.Running);
            });
        var startedDetail = Assert.IsType<JsonElement>(started.Components["workflowRun"].Data);

        Assert.Equal(nameof(WorkflowRunStatus.Running), startedDetail.GetProperty("status").GetString());
        Assert.Equal(
            nameof(WorkerState.Running),
            startedDetail.GetProperty("steps")[0].GetProperty("childSample")[0].GetProperty("state").GetString());

        releaseChild.TrySetResult();
        await handle.WaitForCompletion();
    }

    [Fact]
    public async Task WorkerOverviewWatcherReceivesInitialSnapshotAndLifecycleUpdates()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker-overview";
        var updates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, subscriptionId, updates);
        await connection.StartAsync();

        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            subscriptionId,
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Standard,
                WorkerDuration: WorkComponentShapes.Standard,
                WorkerTimeline: WorkComponentShapes.Standard),
            null);

        var initial = await ReadUntil(
            updates.Reader,
            update => update.Worker?.WorkerId == workerId);

        var initialWorker = Require(initial.Worker);
        Assert.Equal(workerId, initialWorker.WorkerId);
        Assert.Equal(WorkerState.Queued, initialWorker.State);
        Assert.Null(initial.LatestIteration);

        var session = Session(system);
        var worker = await session.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        try
        {
            var start = await session.Workers.Execute(worker.Version, WorkAction.Start);
            Assert.True(start.IsAccepted);

            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var started = await ReadUntil(
                updates.Reader,
                update =>
                    update.Worker?.WorkerId == workerId &&
                    update.Worker.State == WorkerState.Running &&
                    update.LatestIteration?.WorkerId == workerId &&
                    update.LatestIteration.Status == WorkCompletionStatus.Executing);

            var startedWorker = Require(started.Worker);
            var startedIteration = Require(started.LatestIteration);
            Assert.Equal(workerId, startedWorker.WorkerId);
            Assert.Equal(WorkerState.Running, startedWorker.State);
            Assert.Equal(workerId, startedIteration.WorkerId);
            Assert.Equal(WorkCompletionStatus.Executing, startedIteration.Status);

            gate.Release.TrySetResult();
            var completion = await handle.WaitForCompletion();
            Assert.True(completion.IsCompletedSuccessfully);

            var completed = await ReadUntil(
                updates.Reader,
                update =>
                    update.Worker?.WorkerId == workerId &&
                    update.Worker.State == WorkerState.Completed &&
                    update.LatestIteration?.WorkerId == workerId &&
                    update.LatestIteration.Status == WorkCompletionStatus.Completed);

            var completedWorker = Require(completed.Worker);
            var completedIteration = Require(completed.LatestIteration);
            Assert.Equal(WorkerState.Completed, completedWorker.State);
            Assert.Equal(workerId, completedIteration.WorkerId);
            Assert.Equal(WorkCompletionStatus.Completed, completedIteration.Status);
            Assert.NotNull(completedIteration.CompletedAt);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task WorkerOverviewWatcherSendsExactlyOneSeedToInitialAndLateSubscribers()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        var criteria = new WorkWorkerOverviewRealtimeCriteria(WorkerControls: WorkComponentShapes.Standard);
        await using var firstConnection = CreateConnection(host);
        await using var secondConnection = CreateConnection(host);
        var firstUpdates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        var secondUpdates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(firstConnection, "first", firstUpdates);
        CaptureWorkerOverviewUpdates(secondConnection, "second", secondUpdates);
        await firstConnection.StartAsync();
        await secondConnection.StartAsync();

        await firstConnection.InvokeAsync(
            "WatchWorkerOverview",
            "first",
            workerId.Value.ToString("D"),
            criteria,
            null);
        _ = await ReadUntil(firstUpdates.Reader, update => update.Worker?.WorkerId == workerId);
        await AssertNoItem(firstUpdates.Reader, TimeSpan.FromMilliseconds(500));

        await secondConnection.InvokeAsync(
            "WatchWorkerOverview",
            "second",
            workerId.Value.ToString("D"),
            criteria,
            null);
        _ = await ReadUntil(secondUpdates.Reader, update => update.Worker?.WorkerId == workerId);
        await AssertNoItem(secondUpdates.Reader, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task WorkerOverviewWatcherKeepsItsSubscriptionWhenTheWorkerDoesNotExistYet()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var subscriptions = host.Services.GetRequiredService<WorkableRealtimeWorkerOverviewSubscriptions>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker-overview-missing";
        var workerId = WorkerId.New();
        var updates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, subscriptionId, updates);
        await connection.StartAsync();

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            subscriptionId,
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(WorkerControls: WorkComponentShapes.Standard),
            null);

        var subscription = Assert.Single(subscriptions.GetDebugSubscriptions(system));
        Assert.Equal(subscriptionId, subscription.SubscriptionId);
        Assert.Equal(workerId, subscription.WorkerId);
        await AssertNoItem(updates.Reader, TimeSpan.FromMilliseconds(250));

        await connection.InvokeAsync("UnwatchWorkerOverview", subscriptionId, null);
        Assert.Empty(subscriptions.GetDebugSubscriptions(system));
    }

    [Fact]
    public async Task WorkerOverviewWatcherRequestsAFullRefreshWhenTheObservedWorkerIsPurged()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = Session(system);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker-overview-purge";
        var updates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, subscriptionId, updates);
        await connection.StartAsync();
        var handle = await session.Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            subscriptionId,
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(WorkerControls: WorkComponentShapes.Standard),
            null);
        _ = await ReadUntil(updates.Reader, update => update.Worker?.WorkerId == workerId);
        var queued = await session.Query.Worker(workerId) ?? throw new InvalidOperationException("Expected worker.");
        var cancel = await session.Workers.Execute(queued.Version, WorkAction.Cancel);
        Assert.True(cancel.IsAccepted);
        _ = await ReadUntil(updates.Reader, update => update.Worker?.State == WorkerState.Canceled);
        var canceled = await session.Query.Worker(workerId) ?? throw new InvalidOperationException("Expected canceled worker.");

        var purge = await session.Workers.Execute(canceled.Version, WorkAction.Purge);
        var refresh = await ReadUntil(updates.Reader, update => update.RequiresRefresh);

        Assert.True(purge.IsAccepted);
        Assert.Null(await session.Query.Worker(workerId));
        Assert.True(refresh.RequiresRefresh);
        Assert.Contains("refreshed", refresh.RefreshReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HubUnwatchMethodsReleaseViewAndWorkerOverviewSubscriptions()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var viewSubscriptions = host.Services.GetRequiredService<WorkableRealtimeViewSubscriptions>();
        var workerSubscriptions = host.Services.GetRequiredService<WorkableRealtimeWorkerOverviewSubscriptions>();
        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        await connection.InvokeAsync("WatchWorkers", "workers-subscription", null, null);
        await connection.InvokeAsync(
            "WatchWorkerOverview",
            "worker-subscription",
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(),
            null);
        await TestEventually.Until(() =>
            viewSubscriptions.GetDebugSubscriptions(system).Count == 1 &&
            workerSubscriptions.GetDebugSubscriptions(system).Count == 1);

        await connection.InvokeAsync("UnwatchWorkers", "workers-subscription", null);
        await connection.InvokeAsync("UnwatchWorkerOverview", "worker-subscription", null);

        Assert.Empty(viewSubscriptions.GetDebugSubscriptions(system));
        Assert.Empty(workerSubscriptions.GetDebugSubscriptions(system));
    }

    [Fact]
    public async Task HubDisconnectReleasesEverySubscriptionKind()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var events = host.Services.GetRequiredService<WorkableRealtimeEventSubscriptions>();
        var views = host.Services.GetRequiredService<WorkableRealtimeViewSubscriptions>();
        var workers = host.Services.GetRequiredService<WorkableRealtimeWorkerOverviewSubscriptions>();
        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        await using var connection = CreateConnection(host);
        await connection.StartAsync();
        await connection.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
        await connection.InvokeAsync("WatchView", "disconnect-view", "overview", null, null);
        await connection.InvokeAsync(
            "WatchWorkerOverview",
            "disconnect-worker",
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(),
            null);
        await TestEventually.Until(() =>
            events.GetDebugSubscriptions(system).Count == 1 &&
            views.GetDebugSubscriptions(system).Count == 1 &&
            workers.GetDebugSubscriptions(system).Count == 1);

        await connection.StopAsync();

        await TestEventually.Until(() =>
            events.GetDebugSubscriptions(system).Count == 0 &&
            views.GetDebugSubscriptions(system).Count == 0 &&
            workers.GetDebugSubscriptions(system).Count == 0);
    }

    [Fact]
    public async Task WorkerOverviewWatcherUsesChangeStreamWithoutEventStreamSubscription()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var eventStream = GetEventStream(system);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker-overview";
        var updates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, subscriptionId, updates);
        await connection.StartAsync();

        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            subscriptionId,
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Standard),
            null);

        var initial = await ReadUntil(
            updates.Reader,
            update => update.Worker?.WorkerId == workerId);

        Assert.Equal(workerId, Require(initial.Worker).WorkerId);
        Assert.Equal(0, eventStream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task WorkerOverviewWatcherIgnoresUnrelatedWorkerChanges()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = Session(system);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker-overview";
        var updates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, subscriptionId, updates);
        await connection.StartAsync();

        var target = await session.Queue.Enqueue("signalr.worker");
        var targetWorkerId = target.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            subscriptionId,
            targetWorkerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Standard),
            null);

        _ = await ReadUntil(
            updates.Reader,
            update => update.Worker?.WorkerId == targetWorkerId);
        await DrainUntilQuiet(updates.Reader, TimeSpan.FromMilliseconds(250));

        var unrelated = await session.Queue.Enqueue("signalr.worker");
        Assert.NotEqual(targetWorkerId, unrelated.WorkerId);
        await TestEventually.Until(() => session.Diagnostics.ReadModel.PendingUpdateCount == 0);

        await AssertNoItem(updates.Reader, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WorkerOverviewWatcherRejectsInvalidWorkerIds()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchWorkerOverview",
            "worker-panel",
            "not-a-guid",
            new WorkWorkerOverviewRealtimeCriteria(),
            null));

        Assert.Contains("not-a-guid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not valid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerOverviewWatcherRewatchWithNewSubscriptionIdReceivesSubsequentUpdates()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string firstSubscriptionId = "worker-overview-first";
        const string secondSubscriptionId = "worker-overview-second";
        var firstUpdates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        var secondUpdates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, firstSubscriptionId, firstUpdates);
        CaptureWorkerOverviewUpdates(connection, secondSubscriptionId, secondUpdates);
        await connection.StartAsync();

        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerControls: WorkComponentShapes.Standard,
            WorkerDuration: WorkComponentShapes.Standard,
            WorkerTimeline: WorkComponentShapes.Standard);

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            firstSubscriptionId,
            workerId.Value.ToString("D"),
            criteria,
            null);

        await ReadUntil(
            firstUpdates.Reader,
            update => update.Worker?.WorkerId == workerId);

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            secondSubscriptionId,
            workerId.Value.ToString("D"),
            criteria,
            null);

        await ReadUntil(
            secondUpdates.Reader,
            update => update.Worker?.WorkerId == workerId);

        var session = Session(system);
        var worker = await session.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        try
        {
            var start = await session.Workers.Execute(worker.Version, WorkAction.Start);
            Assert.True(start.IsAccepted);

            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var started = await ReadUntil(
                secondUpdates.Reader,
                update =>
                    update.Worker?.WorkerId == workerId &&
                    update.Worker.State == WorkerState.Running &&
                    update.LatestIteration?.WorkerId == workerId &&
                    update.LatestIteration.Status == WorkCompletionStatus.Executing);

            Assert.Equal(WorkerState.Running, Require(started.Worker).State);
            Assert.Equal(WorkCompletionStatus.Executing, Require(started.LatestIteration).Status);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ViewWatcherReceivesDiagnosticsViewOnDiagnosticsInterval()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "queueDiagnostics",
                    "queueDiagnostics",
                    JsonSerializer.SerializeToElement(new { publishMode = "continuous" }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "readModelDiagnostics",
                    "readModelDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningThreshold = 100 }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "retentionDiagnostics",
                    "retentionDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningSeconds = 30 }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "concurrencyDiagnostics",
                    "concurrencyDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningSeconds = 30 }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "durabilityDiagnostics",
                    "durabilityDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        acceptedWorkerWarningSeconds = 30,
                        cleanupWarningSeconds = 30,
                    }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "idempotencyDiagnostics",
                    "idempotencyDiagnostics",
                    JsonSerializer.SerializeToElement(new { publishMode = "continuous" }),
                    WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("readModelDiagnostics"));
        await TestEventually.ClockAfter(initial.GeneratedAt);
        await timers.TickWhenReady(ManualDiagnosticsPublishInterval);
        var updated = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("queueDiagnostics") &&
                view.Components.ContainsKey("readModelDiagnostics") &&
                view.Components.ContainsKey("retentionDiagnostics") &&
                view.Components.ContainsKey("concurrencyDiagnostics") &&
                view.Components.ContainsKey("durabilityDiagnostics") &&
                view.Components.ContainsKey("idempotencyDiagnostics"));
        var queue = Assert.IsType<JsonElement>(updated.Components["queueDiagnostics"].Data);
        var diagnostics = Assert.IsType<JsonElement>(updated.Components["readModelDiagnostics"].Data);
        var retention = Assert.IsType<JsonElement>(updated.Components["retentionDiagnostics"].Data);
        var concurrency = Assert.IsType<JsonElement>(updated.Components["concurrencyDiagnostics"].Data);
        var durability = Assert.IsType<JsonElement>(updated.Components["durabilityDiagnostics"].Data);
        var idempotency = Assert.IsType<JsonElement>(updated.Components["idempotencyDiagnostics"].Data);

        Assert.Equal([
            "queueDiagnostics",
            "readModelDiagnostics",
            "retentionDiagnostics",
            "concurrencyDiagnostics",
            "durabilityDiagnostics",
            "idempotencyDiagnostics",
        ], updated.Components.Keys.ToArray());
        Assert.Equal("compact", updated.Components["queueDiagnostics"].Shape);
        Assert.Equal(0, queue.GetProperty("rejectedWorkCount").GetInt64());
        Assert.False(queue.GetProperty("hasRejectedWork").GetBoolean());
        Assert.Equal(0, queue.GetProperty("alertableRejectedWorkCount").GetInt64());
        Assert.False(queue.GetProperty("hasAlertableRejectedWork").GetBoolean());
        Assert.Equal(JsonValueKind.Null, queue.GetProperty("lastRejectedCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, queue.GetProperty("lastAlertableRejectedCode").ValueKind);
        Assert.Equal("compact", updated.Components["readModelDiagnostics"].Shape);
        Assert.Equal(0, diagnostics.GetProperty("pendingUpdateCount").GetInt64());
        Assert.False(diagnostics.GetProperty("isReadModelBehind").GetBoolean());
        Assert.Equal(100, diagnostics.GetProperty("readModelLagWarningThreshold").GetInt32());
        Assert.False(diagnostics.GetProperty("hasProjectorFailure").GetBoolean());
        Assert.Equal("compact", updated.Components["retentionDiagnostics"].Shape);
        Assert.Equal(0, retention.GetProperty("scheduledPurgeCount").GetInt32());
        Assert.False(retention.GetProperty("isRetentionBehind").GetBoolean());
        Assert.Equal(30, retention.GetProperty("retentionLagWarningSeconds").GetInt32());
        Assert.False(retention.GetProperty("hasSchedulerFailure").GetBoolean());
        Assert.Equal("compact", updated.Components["concurrencyDiagnostics"].Shape);
        Assert.Equal(0, concurrency.GetProperty("deferredStartCount").GetInt32());
        Assert.Equal(0, concurrency.GetProperty("lastDrainReleasedCount").GetInt32());
        Assert.False(concurrency.GetProperty("isConcurrencyBehind").GetBoolean());
        Assert.Equal(30, concurrency.GetProperty("concurrencyLagWarningSeconds").GetInt32());
        Assert.Equal("compact", updated.Components["durabilityDiagnostics"].Shape);
        Assert.Equal(0, durability.GetProperty("acceptedWaiterCount").GetInt32());
        Assert.Equal(0, durability.GetProperty("pendingCleanupCount").GetInt32());
        Assert.False(durability.GetProperty("isAcceptedWorkerMaterializationBehind").GetBoolean());
        Assert.Equal(30, durability.GetProperty("acceptedWorkerWarningSeconds").GetInt32());
        Assert.False(durability.GetProperty("isCleanupBehind").GetBoolean());
        Assert.Equal(30, durability.GetProperty("cleanupWarningSeconds").GetInt32());
        Assert.False(durability.GetProperty("hasReaderFailure").GetBoolean());
        Assert.False(durability.GetProperty("hasLeaseRenewalFailure").GetBoolean());
        Assert.False(durability.GetProperty("hasCleanupFailure").GetBoolean());
        Assert.Equal("compact", updated.Components["idempotencyDiagnostics"].Shape);
        Assert.Equal(0, idempotency.GetProperty("duplicateRejectionCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, idempotency.GetProperty("lastDuplicateRejectedStorage").ValueKind);
    }

    [Fact]
    public async Task DiagnosticsAlertWatcherReceivesACompleteShuttingDownSnapshotBeforeTheSystemStops()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await using var connection = CreateConnection(host);
        const string subscriptionId = "shutdown-diagnostics";
        var updates = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, updates);
        await connection.StartAsync();
        var alertOptions = JsonSerializer.SerializeToElement(new { publishMode = "alertChanges" });
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new("system", "systemDiagnostics", alertOptions, WorkComponentShapes.Compact),
                new("queue", "queueDiagnostics", alertOptions, WorkComponentShapes.Compact),
                new("readModel", "readModelDiagnostics", alertOptions, WorkComponentShapes.Compact),
                new("retention", "retentionDiagnostics", alertOptions, WorkComponentShapes.Compact),
                new("concurrency", "concurrencyDiagnostics", alertOptions, WorkComponentShapes.Compact),
                new("durability", "durabilityDiagnostics", alertOptions, WorkComponentShapes.Compact),
            ]),
            null);
        _ = await ReadUntil(updates.Reader, view => view.Components.ContainsKey("system"));

        var stop = system.Stop(TransportAuthorizationTestSupport.CreateTransportRequestContext(
            description: "Verify realtime shutdown diagnostics."));
        var shuttingDown = await ReadUntil(updates.Reader, view =>
        {
            if (!view.Components.TryGetValue("system", out var component) ||
                component.Data is not JsonElement data)
            {
                return false;
            }

            return data.GetProperty("isShuttingDown").GetBoolean();
        });
        await stop;

        Assert.Equal(
            ["system", "queue", "readModel", "retention", "concurrency", "durability"],
            shuttingDown.Components.Keys.ToArray());
        Assert.All(shuttingDown.Components.Values, component =>
        {
            Assert.Equal("ok", component.Status);
            Assert.Equal(WorkComponentShapes.Compact, component.Shape);
        });
        var systemData = Assert.IsType<JsonElement>(shuttingDown.Components["system"].Data);
        Assert.Equal(JsonValueKind.Null, systemData.GetProperty("systemName").ValueKind);
        Assert.Equal("Stopping", systemData.GetProperty("systemState").GetString());
        Assert.True(systemData.GetProperty("isShuttingDown").GetBoolean());
        Assert.IsType<JsonElement>(shuttingDown.Components["queue"].Data);
        Assert.IsType<JsonElement>(shuttingDown.Components["readModel"].Data);
        Assert.IsType<JsonElement>(shuttingDown.Components["retention"].Data);
        Assert.IsType<JsonElement>(shuttingDown.Components["concurrency"].Data);
        Assert.IsType<JsonElement>(shuttingDown.Components["durability"].Data);
    }

    [Fact]
    public async Task DiagnosticsAlertWatcherReceivesAStoppingSnapshotForApplicationShutdown()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        const string subscriptionId = "host-shutdown-diagnostics";
        var updates = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, updates);
        await connection.StartAsync();
        var alertOptions = JsonSerializer.SerializeToElement(new { publishMode = "alertChanges" });
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new("system", "systemDiagnostics", alertOptions, WorkComponentShapes.Compact),
            ]),
            null);
        _ = await ReadUntil(updates.Reader, view => view.Components.ContainsKey("system"));

        var broadcaster = host.Services
            .GetServices<IHostedService>()
            .OfType<WorkableRealtimeBroadcaster>()
            .Single();
        await broadcaster.BroadcastApplicationStoppingAsync();
        var shuttingDown = await ReadUntil(updates.Reader, view =>
        {
            if (!view.Components.TryGetValue("system", out var component) ||
                component.Data is not JsonElement data)
            {
                return false;
            }

            return data.GetProperty("isShuttingDown").GetBoolean();
        });
        var systemData = Assert.IsType<JsonElement>(shuttingDown.Components["system"].Data);
        Assert.Equal(nameof(WorkSystemState.Stopping), systemData.GetProperty("systemState").GetString());
        Assert.True(systemData.GetProperty("isShuttingDown").GetBoolean());
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherReceivesAlertPayloadWhenSystemCapacityIsReached()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.UseCapacity(new WorkSystemCapacityConfiguration
            {
                MaximumWorkers = 1,
            }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "queueDiagnostics",
                    "queueDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null);
        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("queueDiagnostics"));

        var session = Session(system);
        _ = await session.Queue.Enqueue("signalr.worker");
        var rejected = await session.Queue.Enqueue("signalr.worker");
        Assert.False(rejected.QueueOutcome.IsAccepted);

        var updated = await ReadUntil(
            views.Reader,
            view =>
            {
                if (view.GeneratedAt <= initial.GeneratedAt ||
                    !view.Components.TryGetValue("queueDiagnostics", out var component))
                {
                    return false;
                }

                var diagnostics = Assert.IsType<JsonElement>(component.Data);
                return diagnostics.TryGetProperty("hasAlertableRejectedWork", out var hasRejectedWork) &&
                    hasRejectedWork.GetBoolean();
            });
        var data = Assert.IsType<JsonElement>(updated.Components["queueDiagnostics"].Data);

        Assert.Equal(1, data.GetProperty("rejectedWorkCount").GetInt64());
        Assert.True(data.GetProperty("hasRejectedWork").GetBoolean());
        Assert.Equal(1, data.GetProperty("alertableRejectedWorkCount").GetInt64());
        Assert.True(data.GetProperty("hasAlertableRejectedWork").GetBoolean());
        Assert.Equal("workable.system.capacity_reached", data.GetProperty("lastRejectedCode").GetString());
        Assert.Equal("workable.system.capacity_reached", data.GetProperty("lastAlertableRejectedCode").GetString());
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherReceivesAlertPayloadWhenReadModelFallsBehind()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "readModelDiagnostics",
                    "readModelDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                        warningThreshold = 1,
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null);
        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("readModelDiagnostics"));

        using var enqueueCancellation = new CancellationTokenSource();
        var session = Session(system);
        var enqueuePressure = Enumerable.Range(0, 4)
            .Select(index => Task.Run(async () =>
            {
                while (!enqueueCancellation.IsCancellationRequested)
                {
                    _ = await session.Queue.Enqueue("signalr.view");
                }
            }))
            .ToArray();

        try
        {
            await TestEventually.Until(() => session.Diagnostics.ReadModel.PendingUpdateCount >= 1);

            var updated = await ReadUntil(
                views.Reader,
                view =>
                {
                    if (view.GeneratedAt <= initial.GeneratedAt ||
                        !view.Components.TryGetValue("readModelDiagnostics", out var component))
                    {
                        return false;
                    }

                    var diagnostics = Assert.IsType<JsonElement>(component.Data);
                    return diagnostics.TryGetProperty("isReadModelBehind", out var behind) &&
                        behind.GetBoolean();
                });
            var data = Assert.IsType<JsonElement>(updated.Components["readModelDiagnostics"].Data);

            Assert.True(data.GetProperty("pendingUpdateCount").GetInt64() >= 1);
            Assert.True(data.GetProperty("isReadModelBehind").GetBoolean());
            Assert.Equal(1, data.GetProperty("readModelLagWarningThreshold").GetInt32());
            Assert.False(data.GetProperty("hasProjectorFailure").GetBoolean());
        }
        finally
        {
            enqueueCancellation.Cancel();
            await Task.WhenAll(enqueuePressure).WaitAsync(TimeSpan.FromSeconds(5));

            if (!gate.Release.Task.IsCompleted)
            {
                gate.Release.SetResult();
            }
        }
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherRequiresDiagnosticsPermission()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.UseCapacity(new WorkSystemCapacityConfiguration
            {
                MaximumWorkers = 1,
            }),
            groups: TransportAuthorizationTestSupport.ReadGroups.Concat(TransportAuthorizationTestSupport.OperateGroups));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var viewSubscriptions = host.Services.GetRequiredService<WorkableRealtimeViewSubscriptions>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "queueDiagnostics",
                    "queueDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null));

        Assert.Contains("diagnostics permission", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewSubscriptions.GetDebugSubscriptions(system));

        var session = Session(system);
        _ = await session.Queue.Enqueue("signalr.worker");
        var rejected = await session.Queue.Enqueue("signalr.worker");

        Assert.False(rejected.QueueOutcome.IsAccepted);
        Assert.Empty(viewSubscriptions.GetDebugSubscriptions(system));
    }

    [Fact]
    public async Task NamedSystemWatchRequiresAnySystemAccess()
    {
        using var host = await CreateHost(
            addSignalR: true,
            groups: Array.Empty<string>(),
            configureServices: services => services.AddWorkableSystem("remote", builder =>
            {
                builder.StartWithHost();
                builder.RequireAuthorization();
                builder.ConfigureTransportSystemAuthorization();
                builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.remote"), SuccessfulWork);
            }));
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(),
            "remote"));

        Assert.Contains("system-level access", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IHost> CreateHost(
        bool addSignalR,
        string? hubPath = null,
        Action<WorkableSignalROptions>? configureSignalR = null,
        Action<IWorkSystemBuilder>? configureWorkable = null,
        bool authenticated = true,
        IEnumerable<string>? groups = null,
        Action<IServiceCollection>? configureServices = null,
        ClaimsPrincipal? principal = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization(groups);
                    services.AddSingleton<SignalRWorkGate>();
                    configureServices?.Invoke(services);
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        configureWorkable?.Invoke(builder);
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    if (addSignalR)
                    {
                        services.AddWorkableSignalR(options =>
                        {
                            options.PublishInterval = TimeSpan.FromMilliseconds(50);
                            options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                            configureSignalR?.Invoke(options);
                        });
                    }
                });
                web.Configure(app =>
                {
                    if (authenticated)
                    {
                        app.Use(async (context, next) =>
                        {
                            context.User = principal ?? CreateTransportPrincipal(groups: groups);
                            await next();
                        });
                    }

                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableApi("/workable");
                        if (addSignalR)
                        {
                            endpoints.MapWorkableSignalR(hubPath);
                        }
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static HubConnection CreateConnection(IHost host, string? accessToken = null)
        => new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/workable/realtime",
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.GetTestServer().CreateHandler();
                    if (accessToken is not null)
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                    }
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();

    [Fact]
    public async Task AnonymousSignalRConnectionIsRejected()
    {
        using var host = await CreateHost(addSignalR: true, authenticated: false);
        await using var connection = CreateConnection(host);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task SignalRCanUseExplicitWorkableAuthenticationSchemeWithoutChangingHostDefaultScheme()
    {
        using var host = await CreateExplicitSchemeSignalRHost();
        await using var unauthorized = CreateConnection(host);

        await Assert.ThrowsAnyAsync<Exception>(() => unauthorized.StartAsync());

        await using var authorized = CreateConnection(
            host,
            accessToken: WorkableSchemeAuthenticationTestSupport.WorkableToken);
        await authorized.StartAsync();
        await authorized.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
    }

    [Fact]
    public async Task SignalRUsesWorkableTransportSchemeWhenHostFallbackPolicyTargetsAnotherScheme()
    {
        using var host = await CreateExplicitSchemeSignalRHostWithFallbackPolicy();
        await using var connection = CreateConnection(
            host,
            accessToken: WorkableSchemeAuthenticationTestSupport.WorkableToken);

        await connection.StartAsync();
        await connection.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
    }

    private static async Task<IHost> CreateExplicitSchemeSignalRHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSchemeTestAuthentication();
                    services.AddTransportTestAuthorization();
                    services.AddSingleton<SignalRWorkGate>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    services.AddWorkableSignalR(options =>
                    {
                        options.PublishInterval = TimeSpan.FromMilliseconds(50);
                        options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableApi("/workable");
                        endpoints.MapWorkableSignalR();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateExplicitSchemeSignalRHostWithFallbackPolicy()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSchemeTestAuthentication();
                    services.AddAuthorization(options =>
                    {
                        options.FallbackPolicy = new AuthorizationPolicyBuilder(
                            WorkableSchemeAuthenticationTestSupport.AmbientScheme)
                            .RequireClaim("host-app")
                            .Build();
                    });
                    services.AddTransportTestAuthorization();
                    services.AddSingleton<SignalRWorkGate>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    services.AddWorkableSignalR(options =>
                    {
                        options.PublishInterval = TimeSpan.FromMilliseconds(50);
                        options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableApi("/workable");
                        endpoints.MapWorkableSignalR();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static void CaptureRealtimeEvents(
        HubConnection connection,
        Channel<WorkableRealtimeEvent> events)
    {
        connection.On<WorkableRealtimeEvent>(
            WorkableRealtimeClientMethods.WorkEvent,
            workEvent => events.Writer.TryWrite(workEvent));
        connection.On<WorkableRealtimeEventBatch>(
            WorkableRealtimeClientMethods.WorkEvents,
            batch =>
            {
                foreach (var workEvent in batch.Events)
                {
                    events.Writer.TryWrite(workEvent);
                }
            });
    }

    private static void CaptureRealtimeViews(
        HubConnection connection,
        string subscriptionId,
        Channel<WorkComponentQueryResult> views)
    {
        connection.On<WorkableRealtimeViewEnvelope<WorkComponentQueryResult>>(
            WorkableRealtimeClientMethods.ViewUpdated,
            envelope =>
            {
                if (string.Equals(envelope.SubscriptionId, subscriptionId, StringComparison.Ordinal))
                {
                    views.Writer.TryWrite(envelope.Result);
                }
            });
    }

    private static void CaptureWorkerOverviewUpdates(
        HubConnection connection,
        string subscriptionId,
        Channel<WorkWorkerOverviewRealtimeUpdate> updates)
    {
        connection.On<WorkableRealtimeViewEnvelope<WorkWorkerOverviewRealtimeUpdate>>(
            WorkableRealtimeClientMethods.WorkerOverviewUpdated,
            envelope =>
            {
                if (string.Equals(envelope.SubscriptionId, subscriptionId, StringComparison.Ordinal))
                {
                    updates.Writer.TryWrite(envelope.Result);
                }
            });
    }

    private static async Task<T> ReadUntil<T>(
        ChannelReader<T> reader,
        Func<T, bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in reader.ReadAllAsync(cancellation.Token))
        {
            if (predicate(item))
            {
                return item;
            }
        }

        throw new InvalidOperationException("Expected item was not received.");
    }

    private static bool TryFindWorker(
        WorkComponentQueryResult view,
        WorkerId workerId,
        out JsonElement worker)
    {
        worker = default;
        if (!view.Components.TryGetValue("workerGrid", out var component) ||
            component.Data is not JsonElement data)
        {
            return false;
        }

        foreach (var candidate in data.GetProperty("workers").EnumerateArray())
        {
            if (candidate.GetProperty("id").GetProperty("value").GetGuid() == workerId.Value)
            {
                worker = candidate;
                return true;
            }
        }

        return false;
    }

    private static async Task<IReadOnlyList<T>> ReadStream<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    private static async Task<Exception> ReadStreamFailure<T>(IAsyncEnumerable<T> stream)
        => await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

    private static async Task DrainUntilQuiet<T>(
        ChannelReader<T> reader,
        TimeSpan quietPeriod)
    {
        while (true)
        {
            using var cancellation = new CancellationTokenSource(quietPeriod);
            try
            {
                if (!await reader.WaitToReadAsync(cancellation.Token))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }

            while (reader.TryRead(out _))
            {
            }
        }
    }

    private static async Task AssertNoItem<T>(
        ChannelReader<T> reader,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            Assert.False(await reader.WaitToReadAsync(cancellation.Token), "Expected no item to be received.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static IReadOnlySet<T> Required<T>(IReadOnlySet<T>? values)
        => values ?? throw new InvalidOperationException("Expected values.");

    private static T Require<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private static IWorkSystemSession Session(IWorkSystem system)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.InProcess,
            description: "Use SignalR test session.");

    private static ClaimsPrincipal CreateTransportPrincipal(IEnumerable<string>? groups = null)
        => TransportAuthorizationTestSupport.CreateTransportPrincipal(
            id: "signalr-user-1",
            name: "SignalR User",
            email: "signalr.user@example.test",
            groups: groups);

    private static WorkEventStream GetEventStream(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        var field = system.GetType().GetField("events", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected in-memory event stream field.");
        return Assert.IsType<WorkEventStream>(field.GetValue(system));
    }

    private static System.Text.Json.JsonSerializerOptions JsonOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        var gate = context.Services.GetRequiredService<SignalRWorkGate>();
        gate.Entered.TrySetResult();
        return CompleteWhenReleased(gate, cancellationToken);
    }

    private static async Task<WorkExecutionResult> CompleteWhenReleased(
        SignalRWorkGate gate,
        CancellationToken cancellationToken)
    {
        await gate.Release.Task.WaitAsync(cancellationToken);
        return WorkExecutionResult.Success();
    }

    private sealed class SignalRWorkGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingViewGroupHubLifetimeManager(
        ILogger<DefaultHubLifetimeManager<WorkableRealtimeHub>> logger)
        : DefaultHubLifetimeManager<WorkableRealtimeHub>(logger)
    {
        private readonly TaskCompletionSource groupAddStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource groupAddRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int shouldBlockNextGroupAdd = 1;

        public override async Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref this.shouldBlockNextGroupAdd, 0) == 1)
            {
                this.groupAddStarted.TrySetResult();
                await this.groupAddRelease.Task.WaitAsync(cancellationToken);
            }

            await base.AddToGroupAsync(connectionId, groupName, cancellationToken);
        }

        public Task WaitForBlockedGroupAdd()
            => this.groupAddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseGroupAdd()
            => this.groupAddRelease.TrySetResult();
    }

    private sealed class GapIterationStatusSubscription(WorkIterationStatusGapException exception) :
        IWorkIterationStatusSubscription
    {
        public bool IsDisposed { get; private set; }

        public WorkIterationStatusCompletion? Completion => null;

        public IAsyncEnumerable<WorkIterationStatusItem> Read(CancellationToken cancellationToken = default)
            => ThrowGap(exception, cancellationToken);

        public ValueTask DisposeAsync()
        {
            this.IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        private static async IAsyncEnumerable<WorkIterationStatusItem> ThrowGap(
            WorkIterationStatusGapException gap,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw gap;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class MissingIterationStatusStream : IWorkIterationStatusStream
    {
        public static MissingIterationStatusStream Instance { get; } = new();

        public IWorkIterationStatusSubscription Subscribe(
            WorkerIterationReference iteration,
            long afterSequence = 0)
            => throw new KeyNotFoundException();
    }

    private sealed class LimitedIterationStatusStream(WorkerIterationReference expectedIteration) :
        IWorkIterationStatusStream
    {
        public IWorkIterationStatusSubscription Subscribe(
            WorkerIterationReference iteration,
            long afterSequence = 0)
        {
            Assert.Equal(expectedIteration, iteration);
            throw new WorkIterationStatusSubscriptionLimitException(iteration, 1, isSystemLimit: false);
        }
    }

    private sealed class ManualRealtimeTimerFactory : IWorkableRealtimeTimerFactory
    {
        private readonly object gate = new();
        private readonly Dictionary<TimeSpan, ManualRealtimeTimer> timers = new();

        public IWorkableRealtimeTimer Create(TimeSpan interval)
        {
            lock (this.gate)
            {
                if (!this.timers.TryGetValue(interval, out var timer))
                {
                    timer = new ManualRealtimeTimer();
                    this.timers[interval] = timer;
                }

                return timer;
            }
        }

        public async Task TickWhenReady(TimeSpan interval)
        {
            var timer = await TestEventually.UntilNotNull(
                () => Task.FromResult(this.GetTimer(interval)),
                $"Expected realtime timer for interval {interval} to be created.");
            timer.Tick();
        }

        private ManualRealtimeTimer? GetTimer(TimeSpan interval)
        {
            lock (this.gate)
            {
                return this.timers.TryGetValue(interval, out var timer) ? timer : null;
            }
        }
    }

    private sealed class ManualRealtimeTimer : IWorkableRealtimeTimer
    {
        private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await this.ticks.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        public void Tick()
            => this.ticks.Writer.TryWrite(true);

        public void Dispose()
            => this.ticks.Writer.TryComplete();
    }
}
