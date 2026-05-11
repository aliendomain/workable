namespace Workable;

internal readonly record struct WorkInitializationId(Guid Value)
{
    public static WorkInitializationId New() => new(Guid.NewGuid());
}
