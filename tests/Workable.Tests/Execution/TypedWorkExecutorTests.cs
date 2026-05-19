using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Execution")]
public sealed class TypedWorkExecutorTests
{
    [Fact]
    public async Task TypedExecutorInfersSchemasAndSerializesOutput()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<EchoTypedWork>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.echo");
        var handle = await system.Queue.Enqueue("typed.echo", new EchoInput("hello"));
        var completion = await handle.WaitForCompletion();

        Assert.Contains("message", definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echoed", definition.OutputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkSchema.JsonSchemaDialect202012, definition.InputSchema.SchemaDialect);
        Assert.Equal(WorkSchema.JsonSchemaDialect202012, definition.OutputSchema.SchemaDialect);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"echoed\":5", completion.Output?.Json);
        Assert.Contains(completion.Messages, message => message.Code == "typed.echo.completed" && message.Text == "HELLO");
    }

    [Fact]
    public async Task TypedQueueOverloadsWorkThroughInterfaceByNameAndDefinitionId()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<EchoTypedWork>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.echo");
        IWorkQueueService queue = system.Queue;

        var byName = await queue.Enqueue("typed.echo", new EchoInput("name"));
        var byId = await queue.Enqueue(definition.Id, new EchoInput("id"));
        var nameCompletion = await byName.WaitForCompletion<EchoOutput>();
        var idCompletion = await byId.WaitForCompletion<EchoOutput>();

        Assert.Equal(WorkCompletionStatus.Completed, nameCompletion.Status);
        Assert.Equal(4, nameCompletion.Output?.Echoed);
        Assert.Contains("\"echoed\":4", nameCompletion.RawOutput?.Json);
        Assert.Equal(WorkCompletionStatus.Completed, idCompletion.Status);
        Assert.Equal(2, idCompletion.Output?.Echoed);
        Assert.Contains("\"echoed\":2", idCompletion.RawOutput?.Json);
    }

    [Fact]
    public async Task TypedCompletionPreservesMessagesAndNonCompletedStatus()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("typed.failure"),
                (IWorkExecutionContext context, EchoInput input, CancellationToken cancellationToken) =>
                    Task.FromResult(WorkExecutionResult<EchoOutput>.Failure(
                    [
                        WorkMessage.Error("typed.failed", "Typed work failed.", "input"),
                    ]))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var handle = await system.Queue.Enqueue("typed.failure", new EchoInput("bad"));
        var completion = await handle.WaitForCompletion<EchoOutput>();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Null(completion.Output);
        Assert.Contains(completion.Messages, message => message.Code == "typed.failed");
    }

    [Fact]
    public async Task TypedDelegateInfersSchemasAndExecutes()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("typed.delegate"),
                (IWorkExecutionContext context, EchoInput input, CancellationToken cancellationToken) =>
                    Task.FromResult(WorkExecutionResult<EchoOutput>.Success(new EchoOutput(input.Message.Length)))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.delegate");
        var handle = await system.Queue.Enqueue("typed.delegate", new EchoInput("hello"));
        var completion = await handle.WaitForCompletion();

        Assert.Contains("message", definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echoed", definition.OutputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"echoed\":5", completion.Output?.Json);
    }

    [Fact]
    public async Task TypedInputDelegateCanReturnUntypedResult()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("typed.delegate.input.only"),
                (IWorkExecutionContext context, EchoInput input, CancellationToken cancellationToken) =>
                    Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new { Received = input.Message })))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.delegate.input.only");
        var handle = await system.Queue.Enqueue(definition.Id, new EchoInput("hello"));
        var completion = await handle.WaitForCompletion();

        Assert.Contains("message", definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkSchema.None, definition.OutputSchema);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"received\":\"hello\"", completion.Output?.Json);
    }

    [Fact]
    public async Task TypedInputExecutorCanReturnUntypedResult()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<TypedInputOnlyWork>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.input.only");
        var handle = await system.Queue.Enqueue("typed.input.only", new EchoInput("hello"));
        var completion = await handle.WaitForCompletion();

        Assert.Contains("message", definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkSchema.None, definition.OutputSchema);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"received\":\"hello\"", completion.Output?.Json);
    }

    [Fact]
    public async Task TypedExecutorAcceptsJsonInputPayload()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<EchoTypedWork>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var handle = await system.Queue.Enqueue("typed.echo", WorkInput.FromJson("""{"message":"json"}"""));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"echoed\":4", completion.Output?.Json);
    }

    [Fact]
    public async Task TypedExecutorInvalidJsonReturnsStructuredFailure()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<EchoTypedWork>())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var handle = await system.Queue.Enqueue("typed.echo", WorkInput.FromJson("""{"message":{}}"""));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workable.input.invalid_json");
    }

    [Fact]
    public async Task RawDelegateRegistrationStillAcceptsJsonInput()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("raw.delegate"),
                (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromData(input ?? WorkInput.Empty)))))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "raw.delegate");
        var handle = await system.Queue.Enqueue("raw.delegate", WorkInput.FromJson("""{"message":"raw"}"""));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkSchema.None, definition.InputSchema);
        Assert.Equal(WorkSchema.None, definition.OutputSchema);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Equal("""{"message":"raw"}""", completion.Output?.Json);
    }

    [Fact]
    public async Task RawExecutorRegistrationStillAcceptsJsonInput()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<RawEchoWork>(WorkDefinition.Create("raw.executor")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "raw.executor");
        var handle = await system.Queue.Enqueue("raw.executor", WorkInput.FromJson("""{"message":"raw"}"""));
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkSchema.None, definition.InputSchema);
        Assert.Equal(WorkSchema.None, definition.OutputSchema);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Equal("""{"message":"raw"}""", completion.Output?.Json);
    }

    [Fact]
    public async Task ContributedTypedExecutorInfersSchemasAndExecutes()
    {
        await using var system = new ServiceCollection()
            .AddWorkableWork<ContributedTypedWork>()
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.contributed.executor");
        var handle = await system.Queue.Enqueue("typed.contributed.executor", new EchoInput("contributed"));
        var completion = await handle.WaitForCompletion();

        Assert.Contains("message", definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echoed", definition.OutputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"echoed\":11", completion.Output?.Json);
    }

    [Fact]
    public async Task ContributedTypedDelegateInfersSchemasAndExecutes()
    {
        await using var system = new ServiceCollection()
            .AddWorkableWork(
                WorkDefinition.Create("typed.contributed.delegate"),
                (IWorkExecutionContext context, EchoInput input, CancellationToken cancellationToken) =>
                    Task.FromResult(WorkExecutionResult<EchoOutput>.Success(new EchoOutput(input.Message.Length))))
            .AddWorkableSystem(builder => builder.StartWithHost())
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var definition = RequiredDefinition(system, "typed.contributed.delegate");
        var handle = await system.Queue.Enqueue("typed.contributed.delegate", WorkInput.FromJson("""{"message":"contributed"}"""));
        var completion = await handle.WaitForCompletion();

        Assert.Contains("message", definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("echoed", definition.OutputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        Assert.Contains("\"echoed\":11", completion.Output?.Json);
    }

    [Fact]
    public async Task ExplicitSchemasAreNotReplacedByTypedRegistration()
    {
        var explicitInput = new WorkSchema("""{"type":"object","properties":{"custom":{"type":"string"}}}""");
        var explicitOutput = new WorkSchema("""{"type":"object","properties":{"customOutput":{"type":"string"}}}""");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<ExplicitSchemaWork>(
                WorkDefinition.Create(
                    "typed.explicit.schema",
                    inputSchema: explicitInput,
                    outputSchema: explicitOutput)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var definition = RequiredDefinition(system, "typed.explicit.schema");

        Assert.Same(explicitInput, definition.InputSchema);
        Assert.Same(explicitOutput, definition.OutputSchema);
    }

    [Fact]
    public void ExecutorRegistrationRejectsTypesWithoutExecutorInterface()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<NotAnExecutor>(WorkDefinition.Create("not.executor"))));

        Assert.Contains("must implement", exception.Message);
    }

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private sealed record EchoInput(string Message);

    private sealed record EchoOutput(int Echoed);

    private sealed class NotAnExecutor;

    private sealed class RawEchoWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromData(input ?? WorkInput.Empty)));
    }

    [WorkMetadata("typed.echo", "Typed", "Echoes a typed message.")]
    private sealed class EchoTypedWork : IWorkExecutor<EchoInput, EchoOutput>
    {
        public Task<WorkExecutionResult<EchoOutput>> Execute(
            IWorkExecutionContext context,
            EchoInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult<EchoOutput>.Success(
                new EchoOutput(input.Message.ToUpperInvariant().Length),
                [WorkMessage.Info("typed.echo.completed", input.Message.ToUpperInvariant())]));
    }

    [WorkMetadata("typed.input.only", "Typed", "Receives typed input and returns an untyped result.")]
    private sealed class TypedInputOnlyWork : IWorkExecutor<EchoInput>
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            EchoInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new
            {
                Received = input.Message,
            })));
    }

    private sealed class ExplicitSchemaWork : IWorkExecutor<EchoInput>
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            EchoInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    [WorkMetadata("typed.contributed.executor", "Typed", "Contributed typed work.")]
    private sealed class ContributedTypedWork : IWorkExecutor<EchoInput, EchoOutput>
    {
        public Task<WorkExecutionResult<EchoOutput>> Execute(
            IWorkExecutionContext context,
            EchoInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult<EchoOutput>.Success(new EchoOutput(input.Message.Length)));
    }
}
