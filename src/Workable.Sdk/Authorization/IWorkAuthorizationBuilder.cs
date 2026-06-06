namespace Workable;

public interface IWorkAuthorizationBuilder
{
    IWorkAuthorizationBuilder RequireGroups(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null);

    IWorkAuthorizationBuilder AllowReadToGroups(params string[] groups);

    IWorkAuthorizationBuilder AllowOperateToGroups(params string[] groups);

    IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers();
}
