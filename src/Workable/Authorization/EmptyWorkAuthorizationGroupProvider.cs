namespace Workable;

internal sealed class EmptyWorkAuthorizationGroupProvider : IWorkAuthorizationGroupProvider
{
    public static EmptyWorkAuthorizationGroupProvider Instance { get; } = new();

    private EmptyWorkAuthorizationGroupProvider()
    {
    }

    public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
        => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
