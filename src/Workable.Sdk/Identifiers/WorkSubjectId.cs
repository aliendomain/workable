namespace Workable;
public readonly record struct WorkSubjectId(string Type, string Value) : IWorkKey
{
    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
