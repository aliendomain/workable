namespace Workable;
public readonly record struct WorkConcurrencyKey(string Type, string Value) : IWorkKey
{
    public override string ToString()
        => $"{this.Type}:{this.Value}";
}
