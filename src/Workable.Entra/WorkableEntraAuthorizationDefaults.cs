namespace Workable;

public static class WorkableEntraAuthorizationDefaults
{
    public const string ConfigurationSectionName = "Workable:Entra";

    public const string AuthorityHost = "https://login.microsoftonline.com";

    public const string ScopeClaimType = "scp";

    public const string GroupsClaimType = "groups";

    public const string RolesClaimType = "roles";

    public const string RoleClaimType = "role";

    public const string SignalRAccessTokenQueryStringName = "access_token";

    public const string SignalRHubPath = "/workable/realtime";

    public const string ConnectScope = "Workable.Connect";

    public const string ReadScope = "Workable.Read";

    public const string ExecuteScope = "Workable.Execute";

    public const string DiagnosticsScope = "Workable.Diagnostics";

    public const string ControlScope = "Workable.Control";

    public const string AdminScope = "Workable.Admin";
}
