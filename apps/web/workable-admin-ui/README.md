This is the Workable admin UI.

## Getting Started

From `apps/web/workable-admin-ui`, install dependencies and run the development server:

```bash
npm install
npm run dev
```

Open [http://localhost:3000](http://localhost:3000) with your browser to see the result.

For deployment and load-balancer probes, the admin UI exposes a public health endpoint:

```text
GET /health
```

Example response:

```json
{
  "status": "ok",
  "service": "workable-admin-ui",
  "timestamp": "2026-07-08T16:00:00.000Z"
}
```

## Profile Viewer Filters

The iteration profile viewer classifies nodes by the API's required `instrumentation` field. Use the compact database button to show only `sql.client` nodes or the globe button to show only `http.client` nodes. These two instrumentation filters are mutually exclusive; selecting one replaces the other.

The text search, hotspot threshold, and method-scope filters can be combined with either instrumentation filter. **Ancestor context** controls whether matching SQL or HTTP nodes are shown alone or with their containing scopes. The SQL batch viewer also accepts only `sql.client` nodes before reading their captured command context. The UI does not infer SQL or HTTP identity from labels, provider names, or context payloads.

## Targeted Full Profile Capture

The admin UI can temporarily bypass the bounded automatic SQL, HTTP, and extension instrumentation limit:

- To match future workers across the system, select a system, open **Catalog**, and use **Capture all work** in the top **Full profile capture** card.
- To match future workers for one work type, open **Catalog**, select the definition, and use **Capture this definition**.
- To change one existing worker, open **Workers**, select the worker, expand **Worker controls**, and use **Capture this worker**. The control is a toggle and returns the worker to bounded profiling when disabled.

For global and definition rules, choose how many matching workers to capture and how soon the rule expires. One rule supports 1–1,000 matches and a 1–1,440 minute lifetime; one system can hold at most 1,000 active rules. Those rules affect future accepted workers only. The worker toggle applies to that worker's next execution and does not restart an iteration already running.

The HTTP API still supports actor-id and combined actor/definition rules. The admin UI does not expose actor-rule creation because it cannot enumerate or validate the host application's stable actor ids.

A matching rule enables profiling and sets the worker's capture mode to `Full`. This bypasses only the automatic instrumentation node-count limit. It does not bypass queue authorization, invocation-channel restrictions, HTTP privacy exclusions, SQL parameter redaction, or worker/iteration retention.

The UI needs access to the built-in Workable HTTP surface and diagnostics access to list the rules. Creating or deleting global and definition rules also requires `ControlSystem`; without it, the capture card remains visible but read-only. The card is hidden when the selected system reports that the caller lacks diagnostics access. A system administrator has diagnostics and control permission by default but still cannot queue work unless separately authorized for that work definition. Rule creation and queueing are intentionally separate operations.

## Persistent Execution Diagnostics Capture

Persistent capture is a separate control from targeted full profile capture. It writes expiring iteration logs and, optionally, profiles to a registered execution-diagnostics repository so an agent can inspect the evidence after the in-memory worker snapshot is gone.

- Open a system catalog and use the top **Persistent execution diagnostics** card to capture all work temporarily.
- Open **Catalog**, select a definition, and use the same card to capture only that work type.
- Choose logs-only, `Bounded`, or `Full` profile capture, plus separate active and artifact-retention lifetimes. Both lifetimes must be between one minute and 30 days.
- Stop a rule to prevent future matching iterations from being captured. Existing artifacts retain their original expiry.

The control is usable only when the selected system reports `executionDiagnosticsPersistenceAvailable`. Viewing the state requires diagnostics access; changing it requires `ControlSystem`. The UI manages capture policy, while agents and developers query the resulting evidence through the Workable MCP or HTTP execution-diagnostics APIs. See [Persistent Execution Diagnostics](../../../docs/guides/configuration/execution-diagnostics-persistence.md) for registration, production behavior, data limits, and interpretation guidance.

## Admin UI Security Defaults

The admin UI is default-deny. The page and `/api/workable/*` proxy require authentication unless you explicitly opt into anonymous local use. The proxy does not implement its own operation-level role map; the hosted Workable API remains the authority for whether the current caller may read, operate, configure, run lifecycle actions, or inspect diagnostics.

The admin UI reads one active server-side config file:

- `workable-admin.config.local.json` for local or secret-bearing config. This file is ignored by git.
- `workable-admin.config.json` for an optional second local config file when you want a non-secret server-side config without using environment variables. This file is also ignored by git.

If both files exist, `workable-admin.config.local.json` wins. Environment variables override either file.

You can also point at one explicit config file with `WORKABLE_ADMIN_CONFIG_PATH`, or disable config-file loading entirely with `WORKABLE_ADMIN_CONFIG_DISABLED=true`. Missing, unreadable, or malformed explicit files fail closed. Known JSON fields are runtime type-checked, so values such as a string in place of `allowAnonymous: false` fail configuration instead of being coerced. Production HTTP responses use a generic configuration error so they do not disclose server filesystem paths; the server log retains the diagnostic path.

Choose one authentication provider with `authProvider`:

- `basic` uses the built-in username/password login form, but remains disabled until `basicAuth.enabled` is explicitly `true`.
- `entra` uses Microsoft Entra ID and shows a "Sign in with Microsoft" button.

The browser uses the admin login page at `/login`; successful sign-in creates an HttpOnly, SameSite=Lax session cookie. Production uses a Secure, browser-enforced `__Host-` cookie name so sibling subdomains cannot inject or transplant a session, and rejects duplicate session cookies. Use HTTPS in deployed environments so login credentials, Entra callback data, and session cookies are protected on the network.

The built-in Basic login endpoint accepts at most 16 KiB of JSON or form data and returns `413` for a larger credential request. This fixed authentication bound is separate from `WORKABLE_ADMIN_UI_MAX_BODY_BYTES`, which controls authenticated Workable API proxy requests. Basic authentication is disabled by default even when credentials are present. When explicitly enabled, the shared form/header verifier permits four failed attempts per source address and candidate username in a rolling minute; the fifth failure blocks that source bucket in the server process for one minute and returns `429` with `Retry-After`. Source addresses come from `CF-Connecting-IP`, `X-Vercel-Forwarded-For`, `X-Forwarded-For`, or `X-Real-IP`, so a deployed reverse proxy should still overwrite those headers. Spoofing or omitting them cannot disable the security boundary: an account bucket blocks the twentieth failure for the same candidate username, and a process-wide bucket blocks the hundredth failed credential attempt in a rolling minute. While any applicable bucket is blocked, every credential submission receives the same `429` response without testing whether the submitted password is correct. Successful authentication after the block expires clears its source and account buckets but not the process-wide failure history. These in-process limits are defense in depth; use shared edge rate limiting when multiple admin UI processes serve the same deployment.

### Basic Auth

Basic auth is the easiest local setup. Copy `workable-admin.basic.config.example.json` to `workable-admin.config.local.json` and edit the secrets:

```json
{
  "authProvider": "basic",
  "apiUrl": "http://localhost:61932/fake-auth/system-admin/workable",
  "basicAuth": {
    "enabled": true,
    "username": "admin",
    "password": "replace-with-a-long-random-password"
  },
  "sessionSecret": "replace-with-a-different-long-random-secret",
  "sessionMaxAgeSeconds": 28800,
  "sessionAbsoluteMaxAgeSeconds": 86400
}
```

Keep `workable-admin.config.local.json` outside `public/`; it is read only by the Next.js server.

You can also configure Basic auth with environment variables:

```bash
WORKABLE_ADMIN_UI_AUTH_PROVIDER=basic
WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED=true
WORKABLE_ADMIN_UI_USERNAME=admin
WORKABLE_ADMIN_UI_PASSWORD=replace-with-a-long-random-password
WORKABLE_ADMIN_UI_SESSION_SECRET=replace-with-a-different-long-random-secret
```

`sessionSecret` is required, must be independent from the Basic password, must contain at least 32 UTF-8 bytes, and should be a generated random value. The Basic password is never used as a session-signing key. Sessions remain bound to the current Basic username/password configuration, so rotating either credential invalidates existing sessions without requiring the signing secret to change. The credential binding uses a process-cached password KDF result, so its deliberate guessing cost is paid when the configured credential changes rather than on every session verification. `sessionMaxAgeSeconds` is the renewable idle lifetime and `sessionAbsoluteMaxAgeSeconds` is the non-renewable lifetime from initial sign-in (default `86400`, or 24 hours). Prefer Entra for internet-facing or horizontally scaled deployments; the built-in source-scoped failed-attempt state is deliberately local to each admin UI server process and is not a distributed identity lockout service.

### Microsoft Entra ID

To use Microsoft Entra ID, set `authProvider` to `entra` and configure an Entra app registration with a **Web** redirect URI:

This section configures authentication for the Next.js admin UI and delegated token acquisition by that UI. It is separate from the .NET `Workable.Entra` package. The hosted Workable API must register and configure its own Entra authentication handler, validation, audiences, and endpoint policies; `Workable.Entra` may then interpret the resulting principal but does not validate the token.

```text
http://localhost:3000/api/auth/entra/callback
```

Use your deployed admin UI origin for deployed environments:

```text
https://admin.example.com/api/auth/entra/callback
```

The Entra setup has two app-registration sides:

- the target API app registration for the hosted Workable API
- the admin UI app registration that signs users in and requests delegated tokens for that API

For the target API app registration:

- under the manifest, set `requestedAccessTokenVersion` to `2`
- under **Expose an API**, set the Application ID URI
- a good default is `api://<target-client-id>`
- add a delegated scope such as `workable.access`

That means a typical delegated scope string looks like:

```text
api://00000000-0000-0000-0000-000000000000/workable.access
```

If you want the admin UI client to be pre-authorized for that scope, add the admin UI app registration as an authorized client application for the scope under **Expose an API**.

For the admin UI app registration:

- add the Web redirect URI for `/api/auth/entra/callback`
- under **API permissions**, add delegated permission to the target Workable API scope such as `workable.access`
- grant admin consent if your tenant policy requires it

For a local-only Entra setup, copy `workable-admin.entra.config.example.json` to `workable-admin.config.local.json` and fill in the Entra app registration values:

```json
{
  "authProvider": "entra",
  "apiUrl": "http://localhost:61932/workable",
  "entraId": {
    "tenantId": "00000000-0000-0000-0000-000000000000",
    "clientId": "00000000-0000-0000-0000-000000000000",
    "redirectUri": "http://localhost:3000/api/auth/entra/callback",
    "targetApis": [
      {
        "apiUrl": "https://localhost:7058/workable",
        "scope": "api://00000000-0000-0000-0000-000000000000/workable.access"
      }
    ],
    "allowedEmailDomains": ["example.com"]
  },
  "sessionSecret": "replace-with-a-long-random-session-signing-secret"
}
```

For a non-secret Entra config file, use `workable-admin.config.json` and omit secrets:

```json
{
  "authProvider": "entra",
  "apiUrl": "https://workable.example.com/workable",
  "entraId": {
    "tenantId": "00000000-0000-0000-0000-000000000000",
    "clientId": "00000000-0000-0000-0000-000000000000",
    "redirectUri": "https://admin.example.com/api/auth/entra/callback",
    "targetApis": [
      {
        "apiUrl": "https://workable.example.com/workable",
        "scope": "api://00000000-0000-0000-0000-000000000000/workable.access"
      }
    ],
    "allowedEmailDomains": ["example.com"]
  },
  "sessionMaxAgeSeconds": 28800,
  "sessionAbsoluteMaxAgeSeconds": 86400
}
```

`clientSecret` is optional. `sessionSecret` is required, must contain at least 32 UTF-8 bytes, and should be a generated random secret.

If you also want the admin UI to call Entra-protected hosted Workable APIs, configure `entraId.targetApis` with one entry per host:

- `apiUrl`: the exact Workable HTTP API base URL for that host
- `scope`: the delegated scope string for that API, for example `api://<actually-client-id>/workable.access`

That `scope` value must come from the target API app registration's **Expose an API** page, and the target API should be configured to issue v2 access tokens for it.

`targetApis` is fail-closed configuration. Every entry must be an object with non-empty string `apiUrl` and `scope` values. When `WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON` is present it must be a valid JSON array; malformed JSON or entries stop admin authentication instead of falling back to file configuration or silently dropping entries. Production target API URLs must use HTTPS because the proxy sends delegated bearer tokens to them. HTTP target APIs remain available for development only.

That forwarding is explicit and host-bound. The admin UI only forwards a delegated token to a URL that has a matching `targetApis` entry, even if other URLs are allow-listed for the proxy. Tokens stay out of `localStorage` and `sessionStorage`. The browser keeps only compact, encrypted refresh authority in HttpOnly cookies; delegated access tokens stay in a bounded 256-entry per-process cache keyed by the signed-in session and exact scope, with a 64 KiB limit per token. Servers configured with the same scope reuse one cached access token. Cache loss, eviction, or process restart affects performance only: a valid cookie can reacquire the access token. Logout removes that session's local cache entries and prevents an in-flight refresh from repopulating them, while session and owner validation remain the authorization boundary.

The refresh cookie is bound to the immutable Entra tenant/object identity (falling back to the validated issuer/subject pair), individual signed-in session, client, and complete target configuration. Display names and email addresses are not token-owner keys. A cookie from a different admin identity, authentication provider, later sign-in, or changed target configuration is rejected and cleared. On HTTPS, delegated state uses Secure `__Host-` cookies and duplicate chunks are rejected. Each refresh writes an immutable, uniquely named snapshot with a monotonic rotation version, so reverse-order responses from one refresh chain cannot replace later authority. The next request validates compatible snapshots and expires their predecessors, allowing separate admin UI processes to converge through browser state without requiring a shared cache. Legacy snapshots that contain access tokens are accepted once, seed the bounded server cache, and are immediately replaced with compact state. Each new snapshot has an 8 KiB request-cookie budget independent of the server's header limit. Its lifetime is capped by the signed admin session's remaining absolute lifetime, and invalidated, expired, or locally revoked Entra sessions clear all observed chunks. Successfully rotated cookie state is still returned if the subsequent hosted API request fails, preventing an already-consumed refresh-token rotation from being lost.

Logout adds a signed, immutable, uniquely named host-only tombstone and clears OAuth transaction cookies. Sessions and OAuth transactions record the active tombstones they observed and are rejected if a later tombstone appears, so delayed pre-logout session renewal, delegated-token response, or Entra callback cannot restore its former authority. Expired older tombstones may disappear without shortening sessions created after them. Login and callback responses never write tombstones, concurrent logouts are additive, and the next logout compacts the observed set. The common case validates one small HMAC-protected tombstone, validation is capped at eight, and compaction work occurs only on logout. A same-origin token endpoint feeds SignalR's in-memory `accessTokenFactory` and reports the access token's server-calculated remaining lifetime. Concurrent refreshes for the same bound session are serialized within each server process, and concurrent forced refreshes share the completed exchange; the coordinator is removed as soon as its waiters drain.

The transient OAuth state cookie is authenticated with `sessionSecret`, so a fabricated callback cannot trigger Entra backchannel work. On HTTPS, the state, nonce, verifier, and return-path cookies use the browser-enforced `__Host-` prefix, making them host-only and preventing a sibling subdomain from transplanting another signed OAuth transaction. Duplicate transaction cookies are rejected. Entra metadata and signing-key responses are coalesced and cached for five minutes in bounded per-process caches. A token that names an unknown signing key triggers one coalesced signing-key refresh, with a short retry cooldown to prevent unknown key ids from amplifying outbound requests; a failed refresh leaves the previously valid key set available. Metadata, signing-key, authorization-code, and refresh-token requests have a ten-second deadline and a one-MiB JSON response limit. A shared refresh is not canceled by one disconnected waiter; its fixed deadline still bounds the backchannel operation. Backchannel redirects are not followed. Discovery-provided token and signing-key endpoints must use HTTPS on the configured `authorityHost` origin, preventing metadata from redirecting credentials or server-side fetches to another host.

Existing Entra sessions are checked against the current `allowedEmails` and `allowedEmailDomains` values on every authenticated request. Removing a user from that local policy clears and rejects the session immediately rather than waiting for its cookie to expire.

The Entra integration authenticates access to this admin UI; the hosted Workable API still decides whether each proxied operation is allowed.

Environment variable equivalents are:

```bash
WORKABLE_ADMIN_UI_AUTH_PROVIDER=entra
WORKABLE_ADMIN_ENTRA_TENANT_ID=00000000-0000-0000-0000-000000000000
WORKABLE_ADMIN_ENTRA_CLIENT_ID=00000000-0000-0000-0000-000000000000
WORKABLE_ADMIN_ENTRA_CLIENT_SECRET=replace-with-client-secret
WORKABLE_ADMIN_ENTRA_REDIRECT_URI=https://admin.example.com/api/auth/entra/callback
WORKABLE_ADMIN_ENTRA_AUTHORITY_HOST=https://login.microsoftonline.com
WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON=[{"apiUrl":"https://workable.example.com/workable","scope":"api://00000000-0000-0000-0000-000000000000/workable.access"}]
WORKABLE_ADMIN_ENTRA_ALLOWED_EMAILS=alice@example.com,bob@example.com
WORKABLE_ADMIN_ENTRA_ALLOWED_EMAIL_DOMAINS=example.com
WORKABLE_ADMIN_UI_SESSION_SECRET=replace-with-a-long-random-session-signing-secret
```

`allowedEmails` and `allowedEmailDomains` are optional. If both are empty, any successfully authenticated Entra user is allowed into the admin UI. `authorityHost` defaults to `https://login.microsoftonline.com`.

For local-only experiments you can opt into anonymous admin UI access:

```bash
WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS=true
```

Do not use anonymous mode for deployed environments. Anonymous mode only opens the admin UI/proxy; it does not and must not bypass the hosted Workable API's own authentication and authorization.

## Workable API Proxy

The Next.js route at `/api/workable/*` proxies requests to a Workable HTTP API host.

In production, `WORKABLE_API_URL` must be configured. Local development defaults to:

```text
http://localhost:61932/workable
```

Local development also allows loopback hosts such as `localhost`, `127.0.0.1`, and `::1` so the sample host can be tested without extra setup.

For local HTTPS loopback hosts such as `https://localhost:7058/workable`, the target certificate must be trusted by the machine running the Next.js admin UI server. The proxy does not disable TLS verification for local development targets. If a local ASP.NET Core host uses the default developer certificate, trust it before connecting the admin UI:

```bash
dotnet dev-certs https --trust
```

If a local host still fails after trusting the certificate, inspect the target directly in a browser first and make sure the certificate chain is accepted there.

For deployed environments, add every allowed Workable API base URL explicitly:

```bash
WORKABLE_API_URL=https://workable.example.com/workable
WORKABLE_ALLOWED_API_URLS=https://workable.example.com/workable,https://ops.example.com/workable
```

Additional server-side settings are also available:

```bash
WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME=__Host-workable_admin_session
WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS=28800
WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS=86400
WORKABLE_ADMIN_UI_MAX_BODY_BYTES=1048576
```

The proxy rejects browser-supplied `x-workable-api-url` values that are not configured and refuses redirects from hosted API responses. Configure the final Workable API URL directly rather than an endpoint that redirects. These rules keep the admin UI from becoming an open server-side HTTP proxy when deployed. Explicit numeric security settings must be positive safe integers; malformed environment or JSON values fail configuration closed instead of silently reverting to a default.

Unsafe proxy requests also require a same-origin `Origin` header to reduce CSRF risk when browser credentials are used. The proxy does not forward the admin UI `Authorization` header to the hosted Workable API. When `entraId.targetApis` is configured, the proxy instead forwards a delegated Entra bearer token only to a configured matching hosted API URL. The hosted system must continue to enforce its own authentication and authorization on every Workable adapter surface. If the hosted API rejects a request with `401` or `403`, the admin UI returns that response instead of overriding it with local operation-role logic.

Hosted API response bodies are streamed through the Next.js proxy instead of being accumulated in server memory. Disconnecting the browser also cancels the corresponding hosted API request. When the proxy replaces a hosted bearer `401` with stable configuration guidance, it cancels the discarded hosted response body before returning that local response.

The checked-in `npm run dev` and `npm start` commands launch Node with a 32 KiB maximum HTTP header size. This is defense in depth for normal session and compact refresh cookies, not permission to store access tokens in cookies. A self-hosted deployment should configure its trusted reverse proxy or load balancer to accept the same 32 KiB ceiling while retaining request timeouts, rate limits, and malformed-request filtering. If the deployment bypasses the package scripts and launches Next.js directly, pass Node's `--max-http-header-size=32768` option explicitly. Avoid substantially larger limits: they increase per-connection parsing and memory exposure without fixing cookie growth.

The admin UI accepts realtime hub paths only when the hosted system reports an HTTP(S) hub URL on the same origin as the configured Workable API URL. Cross-origin or non-HTTP(S) hub metadata is ignored by default so a hostile hosted system cannot silently make the browser connect to an arbitrary realtime endpoint. When Entra target-token forwarding is configured, SignalR connections fetch that token from a same-origin admin UI endpoint and keep it only in memory on the browser side. A realtime `401` invalidates the cached token and permits one forced refresh/reconnect attempt. If the replacement token is also rejected, retries stop; if the refresh token requires interactive authentication, the browser returns to sign-in. The Workable SignalR mapper also closes an established connection when the host-selected Workable authentication ticket reaches `ExpiresUtc`, including when that explicit transport scheme differs from the endpoint's ambient scheme; Workable does not configure how the host validates or issues that ticket.

The hosted SignalR application must extract the browser token supplied through SignalR's `accessTokenFactory`. Use the host's existing JWT event/middleware, or add `UseWorkableSignalRAccessTokens()` after routing and before authentication. Do not install both extraction paths. The bridge only promotes the token to an authorization header; the host's authentication handler still owns validation and challenge behavior.

For the admin UI's proxied HTTP API calls, the browser talks only to the Next.js origin, so the hosted Workable HTTP API usually does not need browser CORS for those requests. Realtime is different: the browser connects directly to the hosted SignalR hub URL reported by the Workable host. If that hub is on another origin, the hosted application must configure CORS for the SignalR endpoint, for example with `app.MapWorkableSignalR().RequireCors("WorkableRealtime")`.
