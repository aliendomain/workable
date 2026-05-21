This is the Workable admin UI.

## Getting Started

First, run the development server:

```bash
npm run dev
```

Open [http://localhost:3000](http://localhost:3000) with your browser to see the result.

## Admin UI Security Defaults

The admin UI is default-deny. The page and `/api/workable/*` proxy require authentication unless you explicitly opt into anonymous local use. The proxy does not implement its own operation-level role map; the hosted Workable API remains the authority for whether the current caller may read, operate, configure, run lifecycle actions, or inspect diagnostics.

The admin UI reads one active server-side config file:

- `workable-admin.config.local.json` for local or secret-bearing config. This file is ignored by git.
- `workable-admin.config.json` for shared checked-in config that contains no secrets.

If both files exist, `workable-admin.config.local.json` wins. Environment variables override either file.

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

### Microsoft Entra ID

To use Microsoft Entra ID, set `authProvider` to `entra` and configure an Entra app registration with a **Web** redirect URI:

```text
http://localhost:3000/api/auth/entra/callback
```

Use your deployed admin UI origin for deployed environments:

```text
https://admin.example.com/api/auth/entra/callback
```

For a local-only Entra setup, copy `workable-admin.entra.config.example.json` to `workable-admin.config.local.json` and fill in the Entra app registration values:

```json
{
  "authProvider": "entra",
  "apiUrl": "https://workable.example.com/workable",
  "entraId": {
    "tenantId": "00000000-0000-0000-0000-000000000000",
    "clientId": "00000000-0000-0000-0000-000000000000",
    "clientSecret": "replace-with-client-secret",
    "redirectUri": "https://admin.example.com/api/auth/entra/callback",
    "allowedEmailDomains": ["example.com"]
  },
  "sessionSecret": "replace-with-a-long-random-session-signing-secret"
}
```

For a checked-in Entra config, use `workable-admin.config.json` and omit secrets:

```json
{
  "authProvider": "entra",
  "apiUrl": "https://workable.example.com/workable",
  "entraId": {
    "tenantId": "00000000-0000-0000-0000-000000000000",
    "clientId": "00000000-0000-0000-0000-000000000000",
    "redirectUri": "https://admin.example.com/api/auth/entra/callback",
    "allowedEmailDomains": ["example.com"]
  },
  "sessionMaxAgeSeconds": 28800
}
```

Put `clientSecret` and `sessionSecret` in environment variables or `workable-admin.config.local.json`. `sessionSecret` is required for Entra because there is no Basic password to use as a local session-signing fallback. The Entra integration authenticates access to this admin UI; the hosted Workable API still decides whether each proxied operation is allowed. This implementation does not forward Entra tokens to the hosted Workable API.

Environment variable equivalents are:

```bash
WORKABLE_ADMIN_UI_AUTH_PROVIDER=entra
WORKABLE_ADMIN_ENTRA_TENANT_ID=00000000-0000-0000-0000-000000000000
WORKABLE_ADMIN_ENTRA_CLIENT_ID=00000000-0000-0000-0000-000000000000
WORKABLE_ADMIN_ENTRA_CLIENT_SECRET=replace-with-client-secret
WORKABLE_ADMIN_ENTRA_REDIRECT_URI=https://admin.example.com/api/auth/entra/callback
WORKABLE_ADMIN_ENTRA_ALLOWED_EMAIL_DOMAINS=example.com
WORKABLE_ADMIN_UI_SESSION_SECRET=replace-with-a-long-random-session-signing-secret
```

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

The proxy rejects browser-supplied `x-workable-api-url` values that are not configured. This keeps the admin UI from becoming an open server-side HTTP proxy when deployed.

Unsafe proxy requests also require a same-origin `Origin` header to reduce CSRF risk when browser credentials are used. The proxy does not forward the admin UI `Authorization` header to the hosted Workable API; the hosted system must continue to enforce its own authentication and authorization on every Workable adapter surface. If the hosted API rejects a request with `401` or `403`, the admin UI returns that response instead of overriding it with local operation-role logic.

The admin UI accepts realtime hub paths only when the hosted system reports an HTTP(S) hub URL on the same origin as the configured Workable API URL. Cross-origin or non-HTTP(S) hub metadata is ignored by default so a hostile hosted system cannot silently make the browser connect to an arbitrary realtime endpoint.
