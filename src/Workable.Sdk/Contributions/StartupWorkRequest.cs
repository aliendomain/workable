namespace Workable;

public sealed class StartupWorkRequest
{
    private StartupWorkRequest(
        WorkDefinitionId? definitionId,
        string? name,
        WorkInput? input,
        WorkerOptions? options)
    {
        this.DefinitionId = definitionId;
        this.Name = name;
        this.Input = input;
        this.Options = options;
    }

    public WorkDefinitionId? DefinitionId { get; }

    public string? Name { get; }

    public WorkInput? Input { get; }

    public WorkerOptions? Options { get; }

    public static StartupWorkRequest ForDefinition(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null)
        => new(definitionId, null, input, options);

    public static StartupWorkRequest ForDefinition<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null)
        => ForDefinition(definitionId, ToWorkInput(input), options);

    public static StartupWorkRequest ForName(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(null, name, input, options);
    }

    public static StartupWorkRequest ForName<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null)
        => ForName(name, ToWorkInput(input), options);

    private static WorkInput? ToWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };
}
