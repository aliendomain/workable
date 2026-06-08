namespace Workable;

/// <summary>
/// Describes one work item that a startup work source wants Workable to queue during system startup.
/// </summary>
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

    /// <summary>
    /// Gets the target definition identifier, when the request addresses work by definition id.
    /// </summary>
    public WorkDefinitionId? DefinitionId { get; }

    /// <summary>
    /// Gets the target definition name, when the request addresses work by definition name.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the input payload that should be supplied to the queued worker.
    /// </summary>
    public WorkInput? Input { get; }

    /// <summary>
    /// Gets the worker options that should be applied to the queued worker.
    /// </summary>
    public WorkerOptions? Options { get; }

    /// <summary>
    /// Creates a startup request that targets a definition by id.
    /// </summary>
    /// <param name="definitionId">The identifier of the definition to queue when the system starts.</param>
    /// <param name="input">The raw input payload to supply to the worker, or <see langword="null"/> for no input.</param>
    /// <param name="options">Optional worker options to apply when queueing the startup work.</param>
    /// <returns>A startup request for the specified definition id.</returns>
    public static StartupWorkRequest ForDefinition(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null)
        => new(definitionId, null, input, options);

    /// <summary>
    /// Creates a startup request that targets a definition by id and serializes typed input for the worker.
    /// </summary>
    /// <typeparam name="TInput">The logical input type to serialize into <see cref="WorkInput"/>.</typeparam>
    /// <param name="definitionId">The identifier of the definition to queue when the system starts.</param>
    /// <param name="input">The typed input value to serialize for the worker.</param>
    /// <param name="options">Optional worker options to apply when queueing the startup work.</param>
    /// <returns>A startup request for the specified definition id.</returns>
    public static StartupWorkRequest ForDefinition<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null)
        => ForDefinition(definitionId, ToWorkInput(input), options);

    /// <summary>
    /// Creates a startup request that targets a definition by name.
    /// </summary>
    /// <param name="name">The definition name to queue when the system starts.</param>
    /// <param name="input">The raw input payload to supply to the worker, or <see langword="null"/> for no input.</param>
    /// <param name="options">Optional worker options to apply when queueing the startup work.</param>
    /// <returns>A startup request for the specified definition name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static StartupWorkRequest ForName(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(null, name, input, options);
    }

    /// <summary>
    /// Creates a startup request that targets a definition by name and serializes typed input for the worker.
    /// </summary>
    /// <typeparam name="TInput">The logical input type to serialize into <see cref="WorkInput"/>.</typeparam>
    /// <param name="name">The definition name to queue when the system starts.</param>
    /// <param name="input">The typed input value to serialize for the worker.</param>
    /// <param name="options">Optional worker options to apply when queueing the startup work.</param>
    /// <returns>A startup request for the specified definition name.</returns>
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
