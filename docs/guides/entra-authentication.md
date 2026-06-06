# Microsoft Entra Authentication

Use `Workable.Entra` only when your Workable-facing host should accept Microsoft Entra ID bearer tokens.

This is not a general requirement for Workable. It is one authentication and group-mapping strategy for ASP.NET Core hosts that want Workable HTTP, MCP, SignalR, or custom endpoints to trust Entra-issued tokens.

## What It Does

`Workable.Entra` layers on top of `Workable.AspNetCore`.

When you call `AddWorkableEntraAuthorization`, it:

- configures a JWT bearer handler for Microsoft Entra ID
- registers ASP.NET Core authorization services
- configures Workable's ASP.NET Core integration to use that bearer handler
- maps selected Entra claim types into Workable authorization groups
- allows SignalR browser clients to pass the bearer token through the standard `access_token` query-string mechanism on the Workable hub path

It does not replace Workable's own authorization model. Workable still decides what those groups are allowed to do.

## When To Use It

Use this package when:

- the host should act as a protected API for Workable HTTP, MCP, or SignalR
- the Workable admin UI should call the host with Entra-issued target-audience tokens
- your authorization groups should come from Entra scopes, app roles, or security groups

Do not use it if your host is not using Entra. `Workable.Entra` is optional.

## Minimal Setup

Add the package:

```xml
<PackageReference Include="Workable.Entra" Version="<current-version>" />
```

Before wiring up the host, make sure the target Entra app registration is configured for v2 access tokens:

- Under the app manifest, set `accessTokenAcceptedVersion` to `2`.
- Under **Expose an API**, assign an Application ID URI.
- A good default is `api://<client-id>`, because it stays unique and keeps the audience/scope values predictable.

For example, if the app registration client id is `aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee`, a clean default audience is:

```text
api://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
```

Register it:

```csharp
builder.Services.AddWorkableEntraAuthorization(
    builder.Configuration.GetSection(WorkableEntraAuthorizationDefaults.ConfigurationSectionName));

builder.Services.AddWorkableSystem(workable =>
{
    workable.RequireAuthorization();
    workable.ConfigureAuthorization(auth => auth
        .AllowReadAllWorkToGroups("11111111-2222-3333-4444-555555555555")
        .AllowOperateAllWorkToGroups("11111111-2222-3333-4444-555555555555"));
});
```

Add the middleware:

```csharp
var app = builder.Build();
app.UseRouting();
app.UseWorkableEntraAuthorization();

app.MapWorkableApi("/workable");
app.MapWorkableMcp("/workable/mcp");
app.MapWorkableSignalR("/workable/realtime");
```

`UseWorkableEntraAuthorization()` calls both `UseAuthentication()` and `UseAuthorization()` for you. In the usual Entra setup, you do not need to call those separately just to support the Workable endpoints above.

Minimal configuration:

```json
{
  "Workable": {
    "Entra": {
      "TenantId": "00000000-0000-0000-0000-000000000000",
      "Audience": "api://target-app-client-id"
    }
  }
}
```

That `Audience` value should match the Application ID URI from the target app registration.

When that configured audience is either `api://<client-id>` or the bare `<client-id>` form and the identifier portion is a GUID, `Workable.Entra` automatically accepts the paired form too. That keeps the common Entra audience mismatch from forcing every host to repeat both values in configuration.

## How It Fits Workable Authorization

The important model is:

1. Entra proves who the caller is by validating the bearer token.
2. `Workable.Entra` maps selected token claims into Workable groups.
3. Workable evaluates its normal system and work authorization rules against those group values.

So `Workable.Entra` answers "is this token authentic and what groups should Workable see?" Workable then answers "what may this caller do?"

## Token Flow By Surface

HTTP API and MCP:

- clients send `Authorization: Bearer <token>`
- the token must target the hosted Workable API audience
- Workable uses the bearer principal to create the actor and authorization groups

SignalR:

- browser clients usually pass the same bearer token through the SignalR access-token factory
- `Workable.Entra` accepts that token from the standard `access_token` query-string only on the configured Workable SignalR hub paths
- this is limited to the Workable realtime hub path and negotiate requests, not general HTTP routes

Custom ASP.NET Core endpoints:

- once `UseWorkableEntraAuthorization` is active, your endpoint can use `IWorkRequestContextFactory` exactly like any other ASP.NET Core surface

## Common Group Sources

By default, `Workable.Entra` can map three kinds of Entra claims into Workable groups:

- delegated scopes from `scp`
- app roles from `roles`, `role`, and `ClaimTypes.Role`
- security groups from `groups`

That means a Workable system can authorize against:

- scope values such as `Workable.Read`
- app role values issued by the Entra app registration
- Entra group object ids

Choose the one that matches how your organization already models access.

## Option Surface

`WorkableEntraAuthorizationOptions` supports:

- `TenantId`: required tenant id
- `Audience`: primary API audience
- `AdditionalAudiences`: extra accepted audiences for the same host
- `AuthorityHost`: authority host, defaulting to Microsoft login
- `AuthenticationScheme`: bearer scheme name to register and use
- `MapScopesToWorkableGroups`: include `scp` values as Workable groups
- `MapAppRolesToWorkableGroups`: include app-role values as Workable groups
- `MapGroupsToWorkableGroups`: include Entra group ids as Workable groups
- `AllowSignalRAccessTokensFromQueryString`: allow SignalR browser token flow
- `SignalRAccessTokenQueryStringName`: query-string key used for SignalR access tokens
- `SignalRAccessTokenQueryStringPaths`: absolute app paths where query-string SignalR tokens are allowed

Example:

```csharp
builder.Services.AddWorkableEntraAuthorization(options =>
{
    options.TenantId = "00000000-0000-0000-0000-000000000000";
    options.Audience = "api://workable-api";
    options.AdditionalAudiences.Add("api://workable-api-admin");
    options.AuthenticationScheme = "WorkableBearer";
    options.MapScopesToWorkableGroups = true;
    options.MapAppRolesToWorkableGroups = true;
    options.MapGroupsToWorkableGroups = false;
    options.SignalRAccessTokenQueryStringPaths.Add("/internal/work/realtime");
});
```

## Multiple Audiences

`Audience` is the main accepted audience. `AdditionalAudiences` lets one host accept more than one target-audience token.

That is useful when:

- one host exposes more than one Workable-facing API audience
- migration is in progress from one audience identifier to another
- different clients receive different audience values for the same host

Keep the list tight. This is an allow-list of accepted audiences for Workable bearer auth.

`AdditionalAudiences` is still useful for truly separate audience values. You do not need to duplicate `api://<client-id>` and bare `<client-id>` there when they refer to the same GUID-based app registration, because `Workable.Entra` accepts that pair automatically.

## Custom Authentication Scheme

`AuthenticationScheme` lets Workable register and use a dedicated bearer scheme without replacing your host application's default authentication scheme.

That is important when:

- your site uses cookies or OIDC for the browser
- Workable surfaces use bearer tokens
- both need to coexist in the same ASP.NET Core host

`UseWorkableEntraAuthorization` runs `UseAuthentication()` and `UseAuthorization()`, but Workable's own request-context creation will explicitly authenticate the configured transport scheme when needed.

## Choosing What Becomes A Workable Group

You do not need to map every claim type.

Common approaches:

- use only `groups` when your organization already manages access with Entra groups
- use only `roles` when your API has a clean app-role model
- use `scp` for delegated user flows where scopes are the right capability vocabulary

The best choice is the one that makes your Workable authorization rules legible and stable.

## Admin UI Scenario

When the Workable admin UI talks to an Entra-protected host:

- the admin UI should acquire a target-audience token for the hosted Workable API
- HTTP API requests should send that token in the `Authorization` header
- SignalR should send the same token through the SignalR access-token factory
- Workable authorization should be configured against the exact values those tokens emit

That usually means group object ids, app role strings, or scope values, depending on how the target app registration is configured.

For delegated browser access, the target app registration normally also needs a delegated scope under **Expose an API**, such as:

```text
api://<target-client-id>/workable.access
```

## What It Does Not Do

`Workable.Entra` does not add a separate Entra-specific DSL for authorizing work definitions.

You still use normal Workable authorization:

- system rules through `RequireAuthorization` and `ConfigureAuthorization`
- work rules through attributes or fluent registration

`Workable.Entra` only supplies the authentication and group-mapping strategy behind those rules.

## Related Docs

- See [ASP.NET Core Integration](../concepts/aspnetcore-integration.md) for the lower-level request-context plumbing.
- See [Work Authorization](../concepts/authorization.md) for the Workable rule model.
