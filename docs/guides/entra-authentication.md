# Microsoft Entra Authentication

Use `Workable.Entra` when an ASP.NET Core host already authenticates Microsoft Entra identities and Workable should use those identities for its HTTP, MCP, SignalR, or custom adapter surfaces.

`Workable.Entra` is an adapter for host-owned authentication. It is not an authentication setup package.

## Ownership Boundary

The host owns all authentication decisions, including:

- authentication scheme registration and defaults
- tenant, authority, issuer, and metadata configuration
- accepted audiences, including any v1/v2 audience compatibility
- token signature, issuer, audience, and lifetime validation
- inbound claim mapping
- JWT events, including an `EventsType` supplied through dependency injection
- endpoint authorization policies and challenge behavior
- authentication and authorization middleware ordering

`AddWorkableEntraAuthorization` does not call `AddAuthentication` or `AddJwtBearer`, and it does not read or change `JwtBearerOptions` or `TokenValidationParameters`.

It does register the ordinary `Workable.AspNetCore` integration, ensure ASP.NET Core authorization services are available, and add Workable actor/group claim mappers. It does not define or replace the host's `DefaultPolicy`, `FallbackPolicy`, named policies, default schemes, or challenge schemes.

Workable owns only its integration behavior:

- it recognizes raw and standard ASP.NET Core-mapped Entra claim types
- it uses raw or mapped `oid` first for the Workable actor id, then preserves the host's existing fallback order
- it maps enabled Entra scope, app-role, and security-group claims into Workable authorization groups

## Minimal Setup

First configure Entra authentication exactly as the host requires. For example, a host using Microsoft Identity Web might already contain:

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
```

Then add Workable's Entra interpretation:

```csharp
builder.Services.AddWorkableEntraAuthorization();
builder.Services.AddWorkableHttpApi();
builder.Services.AddWorkableMcpServer();
builder.Services.AddWorkableSignalR();

builder.Services.AddWorkableSystem(workable =>
{
    workable.RequireAuthorization();
    workable.ConfigureAuthorization(auth => auth
        .AllowReadAllWorkToGroups("11111111-2222-3333-4444-555555555555")
        .AllowOperateAllWorkToGroups("11111111-2222-3333-4444-555555555555"));
});
```

The host pipeline remains conventional:

```csharp
var app = builder.Build();
app.UseRouting();
// Optional: use only when host authentication does not already extract SignalR query tokens.
app.UseWorkableSignalRAccessTokens();
app.UseAuthentication();
app.UseAuthorization();

app.MapWorkableApi("/workable");
app.MapWorkableMcp("/workable/mcp");
app.MapWorkableSignalR("/workable/realtime");
```

No `Workable:Entra` configuration section is required for the defaults.

The example assumes the host's authentication registration appears before `builder.Build()`. Adding `Workable.Entra` without a host authentication handler does not make bearer tokens valid and does not populate `HttpContext.User`.

## How Authentication Reaches Workable

By default, Workable uses the authenticated principal produced by the host pipeline. It does not assume that the scheme is named `Bearer`.

If the host already selected a transport scheme through `WorkableAspNetCoreAuthorizationOptions`, the default `AddWorkableEntraAuthorization()` call preserves it. Workable.Entra changes that setting only when its own `AuthenticationScheme` option is explicitly supplied.

If the host has multiple authentication schemes and Workable must explicitly authenticate one existing scheme, select it without asking Workable to configure it:

```csharp
const string HostEntraScheme = "HostEntra";
const string WorkableEntraPolicy = "WorkableEntra";

builder.Services.AddWorkableEntraAuthorization(options =>
{
    options.AuthenticationScheme = HostEntraScheme;
});

builder.Services.AddAuthorization(options =>
    options.AddPolicy(
        WorkableEntraPolicy,
        policy => policy
            .AddAuthenticationSchemes(HostEntraScheme)
            .RequireAuthenticatedUser()));

// Later, when mapping the SignalR adapter:
app.MapWorkableSignalR(
    "/workable/realtime",
    authorizationPolicy: WorkableEntraPolicy);
```

The named scheme and policy must already be registered by the host. Workable neither creates nor modifies them. `AuthenticationScheme` selects the principal used internally for Workable actor and group resolution; the named endpoint policy makes the same host scheme available to ASP.NET Core authorization before the SignalR endpoint executes. Omit both selections when the host's normal principal and default policy already use the intended Entra identity.

The transport and endpoint choices are independent:

| Host shape | Workable principal | Endpoint policy behavior |
| --- | --- | --- |
| One ambient host scheme | Leave `AuthenticationScheme` unset | HTTP, MCP, and SignalR use the host `DefaultPolicy` and the authenticated host principal |
| Explicit existing Workable scheme | Set `AuthenticationScheme` | Workable authenticates that scheme for actor/groups without replacing `HttpContext.User`; the selected host endpoint policy must independently succeed |
| Workable scheme not authenticated by the default policy | Set `AuthenticationScheme` and pass a host-owned named `authorizationPolicy` to each mapped adapter | The named policy owns endpoint authentication and challenges; Workable uses the selected scheme principal internally |
| Host deliberately secures an adapter through `FallbackPolicy` | Optionally set `AuthenticationScheme` and pass `useHostFallbackPolicy: true` | Workable adds no endpoint authorization metadata; the host fallback policy must admit the intended principal |

Selecting a Workable transport scheme is not an authorization-policy bypass. Any host endpoint policy that applies must also succeed.

For HTTP API and MCP authentication failures, Workable invokes the selected scheme's host-owned challenge when one exists. The handler owns the entire response, so clients must not assume every authentication failure is Workable JSON or even a `401`; a cookie/OIDC handler may redirect. SignalR challenge responses belong entirely to the host policy on the endpoint.

## Claims And Workable Authorization

Entra authenticates the caller. `Workable.Entra` then translates claims from the one authenticated identity selected for Workable into the existing Workable authorization model. It never combines actor fields or groups from secondary identities on a composite principal. A host can replace `IWorkClaimsIdentitySelector` when its Workable identity is not the principal's primary identity.

Workable.Entra interprets only an identity identified as Entra. When `AuthenticationScheme` explicitly selects an existing host scheme, the principal produced by that scheme is the Entra identity. In ambient-principal mode, the default classifier requires a raw or standard mapped `oid` claim. Other cookie or custom identities fall through to the host's ordinary Workable actor and group configuration instead of having similarly named role or group claims reinterpreted as Entra claims.

Workable captures the host-selected identity once per adapter request or SignalR connection. A custom
`IWorkClaimsIdentitySelector` may return a normalized or cloned identity; Entra scheme provenance, actor inference, and
group mapping all consume that same captured instance rather than invoking the selector again.

The scheme selection is treated as Entra provenance only for the principal Workable actually obtains from that scheme
for the current HTTP request. Merely configuring `AuthenticationScheme` does not cause an unrelated
`ClaimsPrincipal` passed directly to `IWorkActorFactory.Create(...)` to be reinterpreted as Entra. A directly supplied
principal still needs raw or mapped `oid`, or a host `IdentityPredicate`, for Entra-specific inference.

By default it recognizes:

- delegated scopes from raw or mapped `scp`
- app roles from `roles`, `role`, `ClaimTypes.Role`, and the authenticated identity's configured `RoleClaimType`
- security-group ids from `groups`

Raw and mapped `oid` are placed first in the actor-id lookup order. This gives actor-scoped operations such as `WatchMyWorkers` the stable tenant object id without changing the host's inbound-claim mapping policy. When no object id exists, Workable preserves the host's existing actor-id fallback order, including `NameIdentifier` and then `sub` under the defaults.

An Entra object id is tenant-scoped. A host that accepts identities from multiple tenants and requires a globally tenant-qualified Workable actor id should register an earlier host `IWorkActorClaimsMapper` that combines trusted `tid` and `oid` values. Changing `ActorIdClaimTypes` alone does not override Workable.Entra's intentional `oid` precedence. Actor identity is audit and actor-scoping data; authorization still comes from the host-authenticated identity and Workable authorization rules.

Scope values are split on spaces because an `scp` claim contains a space-delimited scope set. Concrete Entra `roles`, `role`, mapped role, and `groups` claims are otherwise atomic because JSON arrays already arrive as separate claims. A value such as `Billing Admin` or `Region,West` therefore remains one Workable group instead of being reinterpreted through the generic comma separator. A host can deliberately opt a claim type into another wire format through `GroupClaimValueSeparatorsByClaimType`, or own it completely with an earlier claim mapper. A custom host `RoleClaimType` continues to use the host's generic separator behavior when it is not one of the concrete Entra claim types.

These mappings are Entra-identity scoped. `Workable.Entra` does not add or remove actor claim types, group claim types, or separators in `WorkableAspNetCoreAuthorizationOptions`; host-defined actor and group claim mappers run before Entra defaults regardless of registration order. Disabling an Entra mapping consumes the matching Entra claim without adding a Workable group, so it cannot fall through to Workable's generic defaults. A host mapper can still deliberately own that claim because host mappers run first.

Concrete Entra claim names are classified before the selected identity's generic role alias. For example, if a host sets `RoleClaimType` to `groups`, the `groups` claim still follows `MapGroupsToWorkableGroups`; enabling app-role mapping cannot re-enable security-group mapping indirectly.

Workable still decides what those group values may do through normal system and work authorization rules.

## Option Surface

`WorkableEntraAuthorizationOptions` contains only Workable integration settings:

- `AuthenticationScheme`: optional existing host scheme to authenticate explicitly; unset uses the host-produced principal
- `IdentityPredicate`: optional imperative classifier that replaces Workable.Entra's default scheme/`oid` classifier
- `MapScopesToWorkableGroups`: whether raw and mapped `scp` values become Workable groups
- `MapAppRolesToWorkableGroups`: whether Entra app-role values become Workable groups
- `MapGroupsToWorkableGroups`: whether Entra security-group ids become Workable groups

Example:

```csharp
builder.Services.AddWorkableEntraAuthorization(options =>
{
    // Select an existing scheme, or leave this unset to consume the host's ambient principal.
    options.AuthenticationScheme = "HostEntra";
    options.MapScopesToWorkableGroups = true;
    options.MapAppRolesToWorkableGroups = true;
    options.MapGroupsToWorkableGroups = false;
});
```

For an ambient Entra identity that deliberately has no object-id claim, the host can supply its own classifier without changing authentication:

```csharp
builder.Services.AddWorkableEntraAuthorization(options =>
    options.IdentityPredicate = identity =>
        string.Equals(identity.AuthenticationType, "CustomEntra", StringComparison.Ordinal));
```

`IdentityPredicate` is imperative host behavior and is not read from configuration. Supplying it replaces Workable.Entra's default scheme/`oid` classifier for Entra-specific mapping; it is not an additional OR condition. The predicate must therefore return `true` for every identity the host wants Workable.Entra to interpret.

The same requirement applies to an app-only or other host token shape that does not carry `oid`: select the existing Entra scheme through `AuthenticationScheme`, or provide a predicate that recognizes the identity using claims already validated by the host.

The scheme and boolean mapping settings can also come from configuration:

```json
{
  "Workable": {
    "Entra": {
      "AuthenticationScheme": "HostEntra",
      "MapScopesToWorkableGroups": true,
      "MapAppRolesToWorkableGroups": true,
      "MapGroupsToWorkableGroups": false
    }
  }
}
```

```csharp
builder.Services.AddWorkableEntraAuthorization(
    builder.Configuration.GetSection(WorkableEntraAuthorizationDefaults.ConfigurationSectionName));
```

Configuration-supplied boolean values must be `true` or `false`. A malformed value fails registration instead of silently using a default.

The configuration overload recognizes only `AuthenticationScheme`, `MapScopesToWorkableGroups`, `MapAppRolesToWorkableGroups`, and `MapGroupsToWorkableGroups`. Authentication keys such as `TenantId`, `Audience`, `AdditionalAudiences`, `AuthorityHost`, token-validation settings, and SignalR token paths are not Workable.Entra settings. ASP.NET Core configuration binders generally ignore unknown keys, so leaving only those legacy keys under `Workable:Entra` does not configure host authentication and does not produce a usable bearer scheme. Move them to the host authentication library's configuration section.

If explicitly configured registration is intentionally repeated, the final configured call supplies the complete Workable.Entra option set. Claim mapping and scheme selection consume that same final set; configured registrations are not partially merged. A later no-argument `AddWorkableEntraAuthorization()` call is ensure-only and does not reset an earlier host selection. Passing a configuration section with none of the recognized Workable.Entra keys is also ensure-only after an earlier registration, so a missing optional section cannot silently restore default claim mappings or clear an explicit scheme.

## Audience Compatibility

Audience acceptance belongs to the host's Entra authentication configuration. If the host must accept both bare `<client-id>` and `api://<client-id>` audience forms for different Entra token versions, configure that allow-list on the host's authentication handler.

`Workable.Entra` receives only the resulting authenticated principal. It neither broadens nor narrows the host's accepted audiences.

Do not duplicate an audience allow-list under `Workable:Entra`. The handler that validates the token is the only authoritative place for that compatibility policy.

## Choosing What Becomes A Workable Group

You do not need to map every Entra claim source. Common approaches are:

- only `groups` when the organization already manages access with Entra groups
- only app roles when the API has a stable app-role model
- `scp` for delegated user flows where scopes are the appropriate capability vocabulary

Choose the values that make the Workable authorization rules legible and stable.

When Entra emits a group-overage indicator instead of concrete `groups` claims, Workable.Entra does not call Microsoft Graph or expand the omitted memberships. Resolve that condition in host authentication/claims transformation, supply a host claim mapper or group provider, or use app roles or scopes that are present in the token. Do not treat the overage indicator itself as a Workable group.

## Admin UI Scenario

When the Workable admin UI talks to an Entra-protected host:

- the admin UI acquires a target-audience token for the hosted API
- HTTP API requests send it in the `Authorization` header
- SignalR supplies it through its access-token factory; either the host's existing authentication integration extracts it, or the optional `UseWorkableSignalRAccessTokens()` bridge promotes it after routing selects a mapped Workable hub and before host authentication
- the host validates the token using its existing Entra configuration
- Workable authorization evaluates the resulting actor and group claims

This admin application's OAuth client is separate from the `Workable.Entra` host package. Its session, OAuth transaction, and delegated-token state use host-only `__Host-` cookies on HTTPS and reject ambiguous duplicates. Its JSON backchannel responses are size- and time-bounded, redirects are refused, and discovery-provided token and signing-key endpoints must remain on the configured HTTPS authority origin. Delegated target-token state is bound to the immutable validated Entra subject and individual admin session rather than mutable display claims, cannot outlive that session's absolute lifetime, and uses immutable snapshots so reverse-order concurrent responses cannot roll refresh state backward. Production delegated target APIs must use HTTPS. A signed random logout generation invalidates delayed responses from the ended session, including an Entra callback bound to a pre-logout OAuth transaction, without relying on synchronized process clocks or preventing a later sign-in. Only logout writes that generation cookie; login and callback responses do not reissue it and therefore cannot move the browser's logout barrier backward when responses arrive out of order. See the [admin UI authentication guide](../../apps/web/workable-admin-ui/README.md#authentication).

## What It Does Not Do

`Workable.Entra` does not:

- register or configure JWT bearer authentication
- select tenants, issuers, or audiences
- change security validation or inbound claim mapping
- replace host authentication events
- install SignalR query-token middleware, order the host pipeline, or choose hub paths
- add an Entra-specific Workable authorization DSL

System rules still use `RequireAuthorization` and `ConfigureAuthorization`; work rules still use the normal attributes or fluent registration.

## Deployment Checklist

Before exposing a Workable adapter with Entra identities, verify that:

- the host registers the intended authentication handler and validates issuer, audience, signature, and lifetime;
- `UseAuthentication()` and `UseAuthorization()` run before Workable adapter endpoints;
- each adapter's default, named, or fallback policy authenticates the same identity Workable is expected to use;
- browser SignalR query tokens are extracted by exactly one host integration or by `UseWorkableSignalRAccessTokens()` after routing and before authentication;
- CORS for a cross-origin hub uses explicit trusted origins and credentials rather than a wildcard;
- Workable authorization rules use the exact scope, role, or group values emitted by the selected identity;
- logs and reverse proxies redact SignalR query tokens even though Workable excludes query strings from all of its own HTTP-derived request provenance.

## Related Docs

- See [ASP.NET Core Integration](../concepts/aspnetcore-integration.md) for the lower-level request-context plumbing.
- See [Workable Realtime](../adapters/realtime.md) for SignalR browser access-token transport.
- See [Work Authorization](../concepts/authorization.md) for the Workable rule model.
