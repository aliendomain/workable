namespace Workable;

/// <summary>
/// Represents the primary business subject attached to a worker.
/// </summary>
/// <param name="Type">The caller-defined subject namespace.</param>
/// <param name="Value">The subject value within that namespace.</param>
public readonly record struct WorkSubjectId(string Type, string Value) : IWorkKey
{
    /// <summary>
    /// Formats the subject id as <c>type:value</c>.
    /// </summary>
    /// <returns>The formatted subject id.</returns>
    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
