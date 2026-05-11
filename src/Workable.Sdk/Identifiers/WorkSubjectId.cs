namespace Workable;
public readonly record struct WorkSubjectId(string Type, string Value)
{
    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
