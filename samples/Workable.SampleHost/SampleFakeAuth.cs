using System.Security.Claims;

namespace Workable.SampleHost;

internal static class SampleFakeAuth
{
    public const string QueryParameterName = "fakeAuth";
    public const string PathPrefix = "/fake-auth";

    public const string SystemAdministratorGroup = "sample.system-admin";
    public const string WorkAdministratorGroup = "sample.work-admin";

    public const string OperationsConnectGroup = "sample.operations.connect";
    public const string OperationsDiagnosticsGroup = "sample.operations.diagnostics";
    public const string OperationsControlGroup = "sample.operations.control";
    public const string OperationsReadAllGroup = "sample.operations.read-all";
    public const string OperationsOperateAllGroup = "sample.operations.operate-all";
    public const string OperationsCustomReadGroup = "sample.operations.custom.read";
    public const string OperationsCustomOperateGroup = "sample.operations.custom.operate";

    public const string FulfillmentConnectGroup = "sample.fulfillment.connect";
    public const string FulfillmentDiagnosticsGroup = "sample.fulfillment.diagnostics";
    public const string FulfillmentControlGroup = "sample.fulfillment.control";
    public const string FulfillmentReadAllGroup = "sample.fulfillment.read-all";
    public const string FulfillmentOperateAllGroup = "sample.fulfillment.operate-all";
    public const string FulfillmentCustomReadGroup = "sample.fulfillment.custom.read";
    public const string FulfillmentCustomOperateGroup = "sample.fulfillment.custom.operate";

    public static IReadOnlyList<SampleFakeAuthProfile> Profiles { get; } =
    [
        new(
            "anonymous",
            "Anonymous",
            "No authenticated user. Use this to verify Workable returns 401 and the admin UI asks the user to sign in.",
            "Add Server should fail with 401 because the request is unauthenticated.",
            IsAuthenticated: false,
            Groups: []),
        new(
            "no-connect",
            "Rights But No Connect",
            "Authenticated and broadly allowed to read and operate work, but missing Connect on both systems.",
            "Discovery should succeed but return zero systems because this user cannot connect to either system.",
            IsAuthenticated: true,
            Groups:
            [
                OperationsReadAllGroup,
                OperationsOperateAllGroup,
                FulfillmentReadAllGroup,
                FulfillmentOperateAllGroup,
            ]),
        new(
            "operations-only",
            "Connect One System",
            "Can connect to only the default Operations system and has broad work access there.",
            "Discovery should show only the default Operations system.",
            IsAuthenticated: true,
            Groups:
            [
                OperationsConnectGroup,
                OperationsReadAllGroup,
                OperationsOperateAllGroup,
                OperationsDiagnosticsGroup,
                OperationsControlGroup,
            ]),
        new(
            "connect-only",
            "Connect Only",
            "Can discover both systems but has no work read, operate, diagnostics, or control permissions.",
            "Discovery should show both systems, but the system contents should mostly come back empty or denied.",
            IsAuthenticated: true,
            Groups:
            [
                OperationsConnectGroup,
                FulfillmentConnectGroup,
            ]),
        new(
            "system-admin",
            "System Admin",
            "System administrator across both systems. This user should be able to connect, inspect diagnostics, control systems, and operate work everywhere.",
            "Discovery should show both systems and all system-level features should be available.",
            IsAuthenticated: true,
            Groups:
            [
                SystemAdministratorGroup,
            ]),
        new(
            "system-and-work-admin",
            "System + Work Admin",
            "Explicitly carries both the system administrator and work administrator groups across both systems.",
            "Discovery should show both systems with full diagnostics, control, read, and operate access everywhere.",
            IsAuthenticated: true,
            Groups:
            [
                SystemAdministratorGroup,
                WorkAdministratorGroup,
            ]),
        new(
            "work-admin",
            "Work Admin",
            "Work administrator across both systems with enough Connect access to add the host, but without diagnostics or system control permissions.",
            "Discovery should show both systems. Work should be fully visible and operable, but diagnostics and lifecycle control should still be denied.",
            IsAuthenticated: true,
            Groups:
            [
                WorkAdministratorGroup,
                OperationsConnectGroup,
                FulfillmentConnectGroup,
            ]),
        new(
            "custom",
            "Custom Rights",
            "Connects to both systems, can fully read and operate sample.echo on Operations, and can only read fulfillment.picklist.create on Fulfillment.",
            "Discovery should show both systems. Operations should expose only sample.echo to this user, and Fulfillment should expose only fulfillment.picklist.create as read-only.",
            IsAuthenticated: true,
            Groups:
            [
                OperationsConnectGroup,
                FulfillmentConnectGroup,
                OperationsCustomReadGroup,
                OperationsCustomOperateGroup,
                FulfillmentCustomReadGroup,
            ]),
    ];

    public static SampleFakeAuthProfile Resolve(string? id)
        => Profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? Profiles[0];

    public static ClaimsPrincipal CreatePrincipal(string? id)
    {
        var profile = Resolve(id);
        if (!profile.IsAuthenticated)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }

        return new ClaimsPrincipal(new ClaimsIdentity(CreateClaims(profile), authenticationType: "SampleFakeAuth"));
    }

    public static string BuildWorkableApiUrl(string workableApiBaseUrl, string profileId)
    {
        var profile = Resolve(profileId);
        var baseUrl = workableApiBaseUrl.TrimEnd('/');
        return $"{baseUrl}{PathPrefix}/{Uri.EscapeDataString(profile.Id)}/workable";
    }

    public static bool TryApplyPathProfile(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Path.StartsWithSegments(PathPrefix, out var remaining))
        {
            return false;
        }

        var remainingValue = remaining.Value?.TrimStart('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(remainingValue))
        {
            return false;
        }

        var separatorIndex = remainingValue.IndexOf('/');
        var profileSegment = separatorIndex >= 0
            ? remainingValue[..separatorIndex]
            : remainingValue;
        var rewrittenPath = separatorIndex >= 0
            ? $"/{remainingValue[(separatorIndex + 1)..]}"
            : "/";

        var profileId = Uri.UnescapeDataString(profileSegment);
        var profile = Resolve(profileId);
        context.User = CreatePrincipal(profile.Id);
        context.Request.Path = new PathString(rewrittenPath);
        return true;
    }

    public static void ConfigureOperationsSystemAuthorization(this IWorkSystemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAuthorization(authorization => authorization
            .SystemAdministrators(SystemAdministratorGroup)
            .WorkAdministrators(WorkAdministratorGroup)
            .AllowConnectToGroups(OperationsConnectGroup)
            .AllowDiagnosticsToGroups(OperationsDiagnosticsGroup)
            .AllowControlSystemToGroups(OperationsControlGroup)
            .AllowReadAllWorkToGroups(OperationsReadAllGroup)
            .AllowOperateAllWorkToGroups(OperationsOperateAllGroup));
    }

    public static void ConfigureFulfillmentSystemAuthorization(this IWorkSystemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAuthorization(authorization => authorization
            .SystemAdministrators(SystemAdministratorGroup)
            .WorkAdministrators(WorkAdministratorGroup)
            .AllowConnectToGroups(FulfillmentConnectGroup)
            .AllowDiagnosticsToGroups(FulfillmentDiagnosticsGroup)
            .AllowControlSystemToGroups(FulfillmentControlGroup)
            .AllowReadAllWorkToGroups(FulfillmentReadAllGroup)
            .AllowOperateAllWorkToGroups(FulfillmentOperateAllGroup));
    }

    private static IEnumerable<Claim> CreateClaims(SampleFakeAuthProfile profile)
    {
        yield return new Claim(ClaimTypes.NameIdentifier, $"sample-{profile.Id}");
        yield return new Claim(ClaimTypes.Name, profile.Label);
        yield return new Claim(ClaimTypes.Email, $"{profile.Id}@sample.workable.local");

        foreach (var group in profile.Groups)
        {
            yield return new Claim("groups", group);
        }
    }
}

internal sealed record SampleFakeAuthProfile(
    string Id,
    string Label,
    string Description,
    string ExpectedDiscovery,
    bool IsAuthenticated,
    IReadOnlyList<string> Groups);
