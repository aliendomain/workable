namespace Workable;

public interface IWorkSystemAuthorizationBuilder
{
    IWorkSystemAuthorizationBuilder SystemAdministrators(params string[] groups);

    IWorkSystemAuthorizationBuilder WorkAdministrators(params string[] groups);

    IWorkSystemAuthorizationBuilder AllowConnectToGroups(params string[] groups);

    IWorkSystemAuthorizationBuilder AllowDiagnosticsToGroups(params string[] groups);

    IWorkSystemAuthorizationBuilder AllowControlSystemToGroups(params string[] groups);

    IWorkSystemAuthorizationBuilder AllowReadAllWorkToGroups(params string[] groups);

    IWorkSystemAuthorizationBuilder AllowOperateAllWorkToGroups(params string[] groups);
}
