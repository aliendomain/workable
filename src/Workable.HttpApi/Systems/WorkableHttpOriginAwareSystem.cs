namespace Workable;

internal static class WorkableHttpOriginAwareSystem
{
    internal static IOriginAwareWorkSystem Required(IWorkSystem system)
        => system as IOriginAwareWorkSystem
            ?? throw new InvalidOperationException("The configured Workable system does not support trusted work origins.");
}
