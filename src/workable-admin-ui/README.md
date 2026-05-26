This is the Workable admin UI.

## Getting Started

From `src/workable-admin-ui`, install dependencies and run the development server:

```bash
npm install
npm run dev
```

Open [http://localhost:3000](http://localhost:3000) with your browser to see the result.

## Admin UI Security Defaults

The admin UI is default-deny. The page and `/api/workable/*` proxy require authentication unless you explicitly opt into anonymous local use. The proxy does not implement its own operation-level role map; the hosted Workable API remains the authority for whether the current caller may read, operate, configure, run lifecycle actions, or inspect diagnostics.

The admin UI reads one active server-side config file:

- `workable-admin.config.local.json` for local or secret-bearing config. This file is ignored by git.
- `workable-admin.config.json` for an optional second local config file when you want a non-secret server-side config without using environment variables. This file is also ignored by git.

If both files exist, `workable-admin.config.local.json` wins. Environment variables override either file.

You can also point at one explicit config file with `WORKABLE_ADMIN_CONFIG_PATH`, or disable config-file loading entirely with `WORKABLE_ADMIN_CONFIG_DISABLED=true`.

Choose one authentication provider with `authProvider`:

- `basic` uses the built-in username/password login form.
- `entra` uses Microsoft Entra ID and shows a "Sign in with Microsoft" button.

The browser uses the admin login page at `/login`; successful sign-in creates an HttpOnly, SameSite=Lax session cookie. Use HTTPS in deployed environments so login credentials, Entra callback data, and session cookies are protected on the network.

### Basic Auth

Basic auth is the easiest local setup. Copy `workable-admin.basic.config.example.json` to `workable-admin.config.local.json` and edit the secrets:

```json
{
  "authProvider": "basic",
  "apiUrl": "http://localhost:61932/fake-auth/system-admin/workable",
  "basicAuth": {
    "username": "admin",
    "password": "replace-with-a-long-random-password"
  },
  "sessionSecret": "replace-with-a-different-long-random-secret",
  "sessionMaxAgeSeconds": 28800
}
```

Keep `workable-admin.config.local.json` outside `public/`; it is read only by the Next.js server.

You can also configure Basic auth with environment variables:

```bash
WORKABLE_ADMIN_UI_AUTH_PROVIDER=basic
WORKABLE_ADMIN_UI_USERNAME=admin
WORKABLE_ADMIN_UI_PASSWORD=replace-with-a-long-random-password
WORKABLE_ADMIN_UI_SESSION_SECRET=replace-with-a-different-long-random-secret
```

For Basic auth, `sessionSecret` is optional. If it is omitted, the admin UI falls back to the configured Basic password for local session signing.

### Microsoft Entra ID

To use Microsoft Entra ID, set `authProvider` to `entra` and configure an Entra app registration with a **Web** redirect URI:

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
  "sessionMaxAgeSeconds": 28800
}
```

`clientSecret` is optional. `sessionSecret` is required for Entra because there is no Basic password to use as a local session-signing fallback.

If you also want the admin UI to call Entra-protected hosted Workable APIs, configure `entraId.targetApis` with one entry per host:

- `apiUrl`: the exact Workable HTTP API base URL for that host
- `scope`: the delegated scope string for that API, for example `api://<actually-client-id>/workable.access`

That `scope` value must come from the target API app registration's **Expose an API** page, and the target API should be configured to issue v2 access tokens for it.

That forwarding is explicit and host-bound. The admin UI only forwards a delegated token to a URL that has a matching `targetApis` entry, even if other URLs are allow-listed for the proxy. The token stays out of `localStorage` and `sessionStorage`; the Next.js server keeps refresh/access state in encrypted HttpOnly cookies and uses a same-origin token endpoint only to feed SignalR's in-memory `accessTokenFactory`.

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
WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME=workable_admin_session
WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS=28800
WORKABLE_ADMIN_UI_MAX_BODY_BYTES=1048576
```

The proxy rejects browser-supplied `x-workable-api-url` values that are not configured. This keeps the admin UI from becoming an open server-side HTTP proxy when deployed.

Unsafe proxy requests also require a same-origin `Origin` header to reduce CSRF risk when browser credentials are used. The proxy does not forward the admin UI `Authorization` header to the hosted Workable API. When `entraId.targetApis` is configured, the proxy instead forwards a delegated Entra bearer token only to a configured matching hosted API URL. The hosted system must continue to enforce its own authentication and authorization on every Workable adapter surface. If the hosted API rejects a request with `401` or `403`, the admin UI returns that response instead of overriding it with local operation-role logic.

The admin UI accepts realtime hub paths only when the hosted system reports an HTTP(S) hub URL on the same origin as the configured Workable API URL. Cross-origin or non-HTTP(S) hub metadata is ignored by default so a hostile hosted system cannot silently make the browser connect to an arbitrary realtime endpoint. When Entra target-token forwarding is configured, SignalR connections fetch that token from a same-origin admin UI endpoint and keep it only in memory on the browser side.

For the admin UI's proxied HTTP API calls, the browser talks only to the Next.js origin, so the hosted Workable HTTP API usually does not need browser CORS for those requests. Realtime is different: the browser connects directly to the hosted SignalR hub URL reported by the Workable host. If that hub is on another origin, the hosted application must configure CORS for the SignalR endpoint, for example with `app.MapWorkableSignalR().RequireCors("WorkableRealtime")`.
