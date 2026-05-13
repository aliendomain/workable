namespace Workable;

public readonly record struct WorkIdentifier(string Type, string Value) : IWorkKey
{
    public WorkIdentifier(string type, Guid value)
        : this(type, value.ToString("D"))
    {
    }

    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
