namespace Workable;

/// <summary>
/// Identifies the concurrency group a worker should coordinate against.
/// </summary>
/// <param name="Type">The caller-defined concurrency-key namespace.</param>
/// <param name="Value">The concurrency-key value within that namespace.</param>
public readonly record struct WorkConcurrencyKey(string Type, string Value) : IWorkKey
{
    /// <summary>
    /// Formats the key as <c>type:value</c>.
    /// </summary>
    /// <returns>The formatted concurrency key.</returns>
    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
