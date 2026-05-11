namespace Workable;

public sealed class DefaultDotNetWorkOriginProvider : IDotNetWorkOriginProvider
{
    public WorkOrigin CreateOrigin(string description)
        => WorkOrigin.Create(WorkInvocationChannel.DotNet, description: description);
}
