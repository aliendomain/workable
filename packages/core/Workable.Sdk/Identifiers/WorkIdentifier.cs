namespace Workable;

/// <summary>
/// Represents an additional searchable identifier attached to a worker.
/// </summary>
/// <param name="Type">The caller-defined identifier namespace.</param>
/// <param name="Value">The identifier value within that namespace.</param>
public readonly record struct WorkIdentifier(string Type, string Value) : IWorkKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkIdentifier"/> struct from a GUID value.
    /// </summary>
    /// <param name="type">The caller-defined identifier namespace.</param>
    /// <param name="value">The GUID value to format as the identifier value.</param>
    public WorkIdentifier(string type, Guid value)
        : this(type, value.ToString("D"))
    {
    }

    /// <summary>
    /// Formats the identifier as <c>type:value</c>.
    /// </summary>
    /// <returns>The formatted identifier.</returns>
    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
