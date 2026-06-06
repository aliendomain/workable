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
        var queue = new RecordingQueueService();
        var session = new RecordingSession(queue);
        var adapter = new WorkableHttpQueueAdapter();

        var result = await adapter.Enqueue(
            session,
            "http.queue",
            new WorkableHttpWorkRequest(
                input.RootElement,
                Options: new WorkableHttpWorkerOptions(ProfilingEnabled: true),
                SubjectId: subject,
                ConcurrencyKey: concurrency,
                Identifiers: new HashSet<WorkIdentifier> { identifier }));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.Equal("http.queue", queue.Name);
        Assert.Equal("""{"id":"alpha"}""", queue.Input?.Json);
        Assert.Equal(subject, queue.Input?.SubjectId);
        Assert.Equal(concurrency, queue.Input?.ConcurrencyKey);
        Assert.Contains(identifier, queue.Input?.Identifiers ?? new HashSet<WorkIdentifier>());
        Assert.True(queue.Options?.ProfilingEnabled);
    }

    [Fact]
    public async Task EnqueueByNameWithEmptyInputWhenRequestIsMissing()
    {
        var queue = new RecordingQueueService();
        var session = new RecordingSession(queue);
        var adapter = new WorkableHttpQueueAdapter();

        var result = await adapter.Enqueue(session, "http.queue");

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.Equal("http.queue", queue.Name);
        Assert.Equal(WorkInput.Empty, queue.Input);
        Assert.Null(queue.Options);
    }

    [Fact]
    public async Task ReturnRejectedResultWithoutWaitingForCompletion()
    {
        var message = WorkMessage.Error("queue.invalid", "Nope.");
        var handle = RecordingWorkerHandle.Rejected(WorkQueueOutcome.Invalid([message]));
        var queue = new RecordingQueueService(handle);
        var session = new RecordingSession(queue);
        var adapter = new WorkableHttpQueueAdapter();

        var result = await adapter.Enqueue(session, "http.rejected", new WorkableHttpWorkRequest(Completion: WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkStatus.Rejected, result.Status);
        Assert.Same(handle.QueueOutcome, result.QueueOutcome);
        Assert.Null(result.Completion);
        Assert.Null(result.Output);
        Assert.Equal([message], result.Messages);
        Assert.Equal(0, handle.WaitForCompletionCalls);
    }

    [Theory]
    [InlineData(WorkCompletionStatus.Completed, WorkableHttpWorkStatus.Completed)]
    [InlineData(WorkCompletionStatus.Failed, WorkableHttpWorkStatus.Failed)]
    [InlineData(WorkCompletionStatus.Interrupted, WorkableHttpWorkStatus.Interrupted)]
    [InlineData(WorkCompletionStatus.Canceled, WorkableHttpWorkStatus.Canceled)]
    [InlineData(WorkCompletionStatus.Invalid, WorkableHttpWorkStatus.Failed)]
    public async Task MapCompletionStatusWhenWaitingForCompletion(
        WorkCompletionStatus completionStatus,
        WorkableHttpWorkStatus expectedStatus)
    {
        var output = WorkOutput.FromJson("""{"ok":true}""");
        var completion = new WorkCompletion(completionStatus, Worker: null, output, []);
        var handle = RecordingWorkerHandle.Accepted(completion);
        var queue = new RecordingQueueService(handle);
        var session = new RecordingSession(queue);
        var adapter = new WorkableHttpQueueAdapter();

        var result = await adapter.Enqueue(
            session,
            "http.wait",
            new WorkableHttpWorkRequest(Completion: WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Same(completion, result.Completion);
        Assert.Same(output, result.Output);
        Assert.Equal(1, handle.WaitForCompletionCalls);
    }

    [Fact]
    public async Task RejectNullAndBlankInputs()
    {
        var adapter = new WorkableHttpQueueAdapter();
        var session = new RecordingSession(new RecordingQueueService());

        await Assert.ThrowsAsync<ArgumentNullException>(() => adapter.Enqueue(null!, "http.queue"));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.Enqueue(session, " "));
    }

    private sealed class RecordingSession(IWorkQueueService queue) : IWorkSystemSession
    {
        public string? SystemName => throw new NotSupportedException();

        public WorkSystemState SystemState => throw new NotSupportedException();

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public IWorkCatalog Catalog => throw new NotSupportedException();

        public IWorkQueueService Queue { get; } = queue;

        public IWorkerOperations Workers => throw new NotSupportedException();

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();
    }

    private sealed class RecordingQueueService(RecordingWorkerHandle? handle = null) : IWorkQueueService
    {
        private readonly RecordingWorkerHandle handle = handle ?? RecordingWorkerHandle.Accepted();

        public string? Name { get; private set; }

        public WorkInput? Input { get; private set; }

        public WorkerOptions? Options { get; private set; }

        public Task<IWorkerHandle> Enqueue(
            string name,
            WorkInput? input = null,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Name = name;
            this.Input = input;
            this.Options = options;

            return Task.FromResult<IWorkerHandle>(this.handle);
        }

        public Task<IWorkerHandle> Enqueue<TInput>(
            string name,
            TInput input,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingWorkerHandle(
        WorkQueueOutcome queueOutcome,
        WorkCompletion? completion) : IWorkerHandle
    {
        private readonly WorkCompletion? completion = completion;

        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId => this.QueueOutcome.WorkerId;

        public int WaitForCompletionCalls { get; private set; }

        public static RecordingWorkerHandle Accepted(WorkCompletion? completion = null)
        {
            var workerId = global::Workable.WorkerId.New();
            return new(
                WorkQueueOutcome.Accepted(workerId),
                completion ?? new WorkCompletion(WorkCompletionStatus.Completed, Worker: null, WorkOutput.Empty, []));
        }

        public static RecordingWorkerHandle Rejected(WorkQueueOutcome queueOutcome)
            => new(queueOutcome, completion: null);

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
        {
            this.WaitForCompletionCalls++;
            return Task.FromResult(this.completion ?? new WorkCompletion(WorkCompletionStatus.Failed, Worker: null, null, this.QueueOutcome.Messages));
        }

        public Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
