using System.Text.Json;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpQueueAdapterShould
{
    [Fact]
    public async Task EnqueueByNameWithInputMetadataAndOptions()
    {
        using var input = JsonDocument.Parse("""{"id":"alpha"}""");
        var subject = new WorkSubjectId("account", "123");
        var concurrency = new WorkConcurrencyKey("tenant", "west");
        var identifier = new WorkIdentifier("invoice", "456");
        var commands = new RecordingCommandDispatcher();
        var adapter = new WorkableHttpQueueAdapter(commands);

        var result = await adapter.Enqueue(
            systemName: null,
            "http.queue",
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi),
            new WorkableHttpWorkRequest(
                input.RootElement,
                Options: new WorkableHttpWorkerOptions(ProfilingEnabled: true),
                SubjectId: subject,
                ConcurrencyKey: concurrency,
                Identifiers: new HashSet<WorkIdentifier> { identifier }));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.Equal("http.queue", commands.WorkName);
        Assert.Equal("""{"id":"alpha"}""", commands.Input?.Json);
        Assert.Equal(subject, commands.Input?.SubjectId);
        Assert.Equal(concurrency, commands.Input?.ConcurrencyKey);
        Assert.Contains(identifier, commands.Input?.Identifiers ?? new HashSet<WorkIdentifier>());
        Assert.True(commands.Options?.WorkerOptions?.ProfilingEnabled);
    }

    [Fact]
    public async Task EnqueueByNameWithEmptyInputWhenRequestIsMissing()
    {
        var commands = new RecordingCommandDispatcher();
        var adapter = new WorkableHttpQueueAdapter(commands);

        var result = await adapter.Enqueue(
            systemName: null,
            "http.queue",
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.Equal("http.queue", commands.WorkName);
        Assert.Equal(WorkInput.Empty, commands.Input);
        Assert.Null(commands.Options?.WorkerOptions);
    }

    [Fact]
    public async Task ConfigurationOnlyOptionsDoNotExplicitlyDisableProfiling()
    {
        var commands = new RecordingCommandDispatcher();
        var adapter = new WorkableHttpQueueAdapter(commands);

        var result = await adapter.Enqueue(
            systemName: null,
            "http.queue",
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi),
            new WorkableHttpWorkRequest(
                Options: new WorkableHttpWorkerOptions
                {
                    Configuration = WorkableHttpWorkConfiguration.From(WorkConfiguration.Default with
                    {
                        Start = WorkStartConfiguration.DoNotStart,
                    }),
                }));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.NotNull(commands.Options?.WorkerOptions);
        Assert.False(commands.Options.WorkerOptions.HasExplicitProfilingEnabled);
        Assert.Equal(WorkStartPolicy.DoNotStart, commands.Options.WorkerOptions.Configuration?.Start.Policy);
    }

    [Fact]
    public async Task ReturnRejectedResultWithoutWaitingForCompletion()
    {
        var message = WorkMessage.Error("queue.invalid", "Nope.");
        var queueOutcome = WorkQueueOutcome.Invalid([message]);
        var commands = new RecordingCommandDispatcher(
            status: WorkDispatchStatus.Invalid,
            queueOutcome: queueOutcome,
            messages: [message]);
        var adapter = new WorkableHttpQueueAdapter(commands);

        var result = await adapter.Enqueue(
            systemName: null,
            "http.rejected",
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi),
            new WorkableHttpWorkRequest(Completion: WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkStatus.Rejected, result.Status);
        Assert.Same(queueOutcome, result.QueueOutcome);
        Assert.Null(result.Completion);
        Assert.Null(result.Output);
        Assert.Equal([message], result.Messages);
    }

    [Theory]
    [InlineData(WorkCompletionStatus.Completed, WorkableHttpWorkStatus.Completed, WorkDispatchStatus.Completed)]
    [InlineData(WorkCompletionStatus.Failed, WorkableHttpWorkStatus.Failed, WorkDispatchStatus.Failed)]
    [InlineData(WorkCompletionStatus.Interrupted, WorkableHttpWorkStatus.Interrupted, WorkDispatchStatus.Interrupted)]
    [InlineData(WorkCompletionStatus.Canceled, WorkableHttpWorkStatus.Canceled, WorkDispatchStatus.Canceled)]
    [InlineData(WorkCompletionStatus.Invalid, WorkableHttpWorkStatus.Failed, WorkDispatchStatus.Invalid)]
    public async Task MapCompletionStatusWhenWaitingForCompletion(
        WorkCompletionStatus completionStatus,
        WorkableHttpWorkStatus expectedStatus,
        WorkDispatchStatus dispatchStatus)
    {
        var output = WorkOutput.FromJson("""{"ok":true}""");
        var completion = new WorkCompletion(completionStatus, Worker: null, output, []);
        var workerId = global::Workable.WorkerId.New();
        var commands = new RecordingCommandDispatcher(
            status: dispatchStatus,
            queueOutcome: WorkQueueOutcome.Accepted(workerId),
            completion: completion,
            workerId: workerId);
        var adapter = new WorkableHttpQueueAdapter(commands);

        var result = await adapter.Enqueue(
            systemName: null,
            "http.wait",
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi),
            new WorkableHttpWorkRequest(Completion: WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(expectedStatus, result.Status);
        Assert.NotNull(result.Completion);
        Assert.Same(output, result.Output);
    }

    [Fact]
    public async Task RejectNullAndBlankInputs()
    {
        var adapter = new WorkableHttpQueueAdapter(new RecordingCommandDispatcher());
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.HttpApi);

        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.Enqueue(null, "http.queue", null!));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.Enqueue(null, " ", requestContext));
    }

    private sealed class RecordingCommandDispatcher(
        WorkDispatchStatus status = WorkDispatchStatus.Accepted,
        WorkQueueOutcome? queueOutcome = null,
        WorkCompletion? completion = null,
        IReadOnlyList<WorkMessage>? messages = null,
        WorkerId? workerId = null) : IWorkCommandDispatcher
    {
        private readonly WorkDispatchStatus status = status;
        private readonly WorkQueueOutcome queueOutcome = queueOutcome ?? WorkQueueOutcome.Accepted(workerId ?? global::Workable.WorkerId.New());
        private readonly WorkCompletion? completion = completion;
        private readonly IReadOnlyList<WorkMessage> messages = messages ?? completion?.Messages ?? queueOutcome?.Messages ?? [];

        public string? SystemName { get; private set; }

        public string? WorkName { get; private set; }

        public WorkRequestContext? RequestContext { get; private set; }

        public WorkInput? Input { get; private set; }

        public WorkDispatchOptions? Options { get; private set; }

        public Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
            string workName,
            TRequest request,
            WorkRequestContext requestContext,
            WorkDispatchOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.Dispatch<TRequest, TResponse>(
                systemName: null,
                workName,
                request,
                requestContext,
                options,
                cancellationToken);

        public Task<WorkDispatchResult<TResponse>> Dispatch<TRequest, TResponse>(
            string? systemName,
            string workName,
            TRequest request,
            WorkRequestContext requestContext,
            WorkDispatchOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.SystemName = systemName;
            this.WorkName = workName;
            this.RequestContext = requestContext;
            this.Options = options;
            this.Input = request as WorkInput;

            return Task.FromResult(new WorkDispatchResult<TResponse>(
                this.status,
                Response: default,
                WorkerId: this.queueOutcome.WorkerId,
                ErrorCode: this.messages.FirstOrDefault()?.Code,
                ErrorMessage: this.messages.FirstOrDefault()?.Text,
                Messages: this.messages,
                QueueOutcome: this.queueOutcome,
                Completion: this.completion?.ToTyped<TResponse>()));
        }
    }
}
