namespace Workable;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkAuthorizationAttribute : Attribute
{
    public string[]? ReadGroups { get; init; }

    public string[]? OperateGroups { get; init; }
}
