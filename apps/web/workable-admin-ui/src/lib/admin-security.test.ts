import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  authenticateAdminRequest,
  createEntraAuthorizationResponse,
  createEntraTargetAccessTokenResponse,
  createAdminSessionCookie,
  createWorkableTargetUrl,
  getAdminAuthProvider,
  validateUnsafeRequestOrigin,
  type AdminSecurityEnvironment,
} from "./admin-security.ts";
import { getAdminSecuritySettings } from "./admin-security/config.ts";
import {
  createExpiredEntraTargetTokenCookies,
  createEntraTargetTokenCookieHeaders,
} from "./admin-security/entra-downstream.ts";
import { createWorkableRealtimeUrl } from "./workable.ts";
import { proxyWorkableRequest } from "./workable-proxy.ts";

test("admin UI authentication fails closed when credentials are not configured", () => {
  const result = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
  });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
});

test("admin UI rejects unauthenticated users by default when credentials are configured", () => {
  const result = authenticateAdminRequest(new Headers(), secureEnv());

  assert.equal(result.ok, false);
  assert.equal(result.status, 401);
  assert.equal(result.headers?.["www-authenticate"], undefined);
});

test("admin UI anonymous mode is rejected in production", () => {
  const result = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
    WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS: "true",
  });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
});

test("login-created session cookie authenticates without browser basic auth", () => {
  const cookie = createAdminSessionCookie(
    "admin",
    new Request("https://admin.example.com/api/auth/login", {
      method: "POST",
    }),
    secureEnv()
  );

  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const authentication = authenticateAdminRequest(
    new Headers({
      cookie: cookie.header.split(";")[0] ?? "",
    }),
    secureEnv()
  );

  assert.equal(authentication.ok, true);
  if (!authentication.ok) {
    return;
  }

  assert.equal(authentication.identity.scheme, "session");
});

test("near-expiry session authentication renews the session cookie", () => {
  const env = secureEnv({
    WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS: "60",
  });
  const loginRequest = new Request("https://admin.example.com/api/auth/login", {
    method: "POST",
  });
  const cookie = createAdminSessionCookie("admin", loginRequest, env);

  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const authentication = authenticateAdminRequest(
    new Headers({
      cookie: cookie.header.split(";")[0] ?? "",
    }),
    env,
    new Request("https://admin.example.com/workers")
  );

  assert.equal(authentication.ok, true);
  if (!authentication.ok) {
    return;
  }

  assert.match(authentication.sessionCookieHeader ?? "", /^workable_admin_session=/);
});

test("expired session cookies are cleared when authentication fails", () => {
  const env = secureEnv({
    WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS: "60",
  });
  const loginRequest = new Request("https://admin.example.com/api/auth/login", {
    method: "POST",
  });
  const cookie = createAdminSessionCookie("admin", loginRequest, env);

  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const originalNow = Date.now;
  Date.now = () => originalNow() + 61_000;
  try {
    const authentication = authenticateAdminRequest(
      new Headers({
        cookie: cookie.header.split(";")[0] ?? "",
      }),
      env
    );

    assert.equal(authentication.ok, false);
    assert.equal(authentication.status, 401);
    assert.match(authentication.headers?.["set-cookie"] ?? "", /workable_admin_session=/);
    assert.match(authentication.headers?.["set-cookie"] ?? "", /Max-Age=0/i);
  } finally {
    Date.now = originalNow;
  }
});

test("admin UI auth provider defaults to basic and can select Entra", () => {
  assert.equal(getAdminAuthProvider(secureEnv()), "basic");
  assert.equal(getAdminAuthProvider(entraEnv()), "entra");
  assert.equal(
    getAdminAuthProvider(entraEnv({ WORKABLE_ADMIN_UI_AUTH_PROVIDER: "entry" })),
    "entra"
  );
});

test("Entra authentication fails closed when required settings are missing", () => {
  const result = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
    WORKABLE_ADMIN_UI_AUTH_PROVIDER: "entra",
    WORKABLE_ADMIN_ENTRA_TENANT_ID: "tenant-id",
    WORKABLE_ADMIN_ENTRA_CLIENT_ID: "client-id",
  });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
});

test("sessions minted for Basic are rejected after switching to Entra", () => {
  const cookie = createAdminSessionCookie(
    "admin",
    new Request("https://admin.example.com/api/auth/login", {
      method: "POST",
    }),
    secureEnv({
      WORKABLE_ADMIN_UI_SESSION_SECRET: "shared-session-secret",
    })
  );

  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const authentication = authenticateAdminRequest(
    new Headers({
      cookie: cookie.header.split(";")[0] ?? "",
    }),
    entraEnv({
      WORKABLE_ADMIN_UI_SESSION_SECRET: "shared-session-secret",
    })
  );

  assert.equal(authentication.ok, false);
  assert.equal(authentication.status, 401);
});

test("Entra login redirects to Microsoft with state, nonce, and PKCE cookies", () => {
  const response = createEntraAuthorizationResponse(
    new Request("https://admin.example.com/api/auth/entra/login?next=/systems"),
    entraEnv()
  );

  assert.equal(response.status, 302);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  const location = response.headers.get("location");
  assert.ok(location);
  if (!location) {
    return;
  }

  const target = new URL(location);
  assert.equal(target.origin, "https://login.microsoftonline.com");
  assert.equal(target.pathname, "/tenant-id/oauth2/v2.0/authorize");
  assert.equal(target.searchParams.get("client_id"), "client-id");
  assert.equal(target.searchParams.get("response_type"), "code");
  assert.equal(target.searchParams.get("code_challenge_method"), "S256");
  assert.ok(target.searchParams.get("state"));
  assert.ok(target.searchParams.get("nonce"));

  const setCookies = getSetCookies(response.headers);
  assert.ok(setCookies.some((cookie) => cookie.startsWith("workable_admin_entra_state=")));
  assert.ok(setCookies.some((cookie) => cookie.startsWith("workable_admin_entra_nonce=")));
  assert.ok(setCookies.some((cookie) => cookie.startsWith("workable_admin_entra_verifier=")));
  assert.ok(setCookies.some((cookie) => cookie.startsWith("workable_admin_entra_next=")));
});

test("Entra login requests offline access and the configured hosted API scope", () => {
  const response = createEntraAuthorizationResponse(
    new Request("https://admin.example.com/api/auth/entra/login"),
    entraEnv({
      WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
        {
          apiUrl: "https://workable.example.com/workable",
          scope: "api://actually-client-id/workable.access",
        },
      ]),
    })
  );

  const location = response.headers.get("location");
  assert.ok(location);
  if (!location) {
    return;
  }

  const scope = new URL(location).searchParams.get("scope") ?? "";
  assert.match(scope, /\boffline_access\b/);
  assert.match(scope, /\bapi:\/\/actually-client-id\/workable\.access\b/);
});

test("Entra login with multiple hosted APIs requests offline access without pinning one target scope", () => {
  const response = createEntraAuthorizationResponse(
    new Request("https://admin.example.com/api/auth/entra/login"),
    entraEnv({
      WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
        {
          apiUrl: "https://workable.example.com/workable",
          scope: "api://actually-client-id/workable.access",
        },
        {
          apiUrl: "https://ops.example.com/workable",
          scope: "api://ops-client-id/workable.access",
        },
      ]),
    })
  );

  const location = response.headers.get("location");
  assert.ok(location);
  if (!location) {
    return;
  }

  const scope = new URL(location).searchParams.get("scope") ?? "";
  assert.equal(scope, "openid profile email offline_access");
});

test("hosted Workable token endpoint returns 200 with no token when no binding is configured for the URL", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
    ]),
  });
  const cookie = createAdminSessionCookie(
    "admin",
    new Request("https://admin.example.com/api/auth/entra/login"),
    env,
    "entra"
  );

  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const response = await createEntraTargetAccessTokenResponse(
    new Request("https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fops.example.com%2Fworkable", {
      headers: {
        cookie: cookie.header.split(";")[0] ?? "",
      },
    }),
    env
  );

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.deepEqual(await response.json(), {
    accessToken: null,
  });
});

test("authenticated proxy access does not require local operation-role configuration", () => {
  const authentication = authenticateAdminRequest(
    new Headers({ authorization: basic("admin", "correct horse battery staple") }),
    secureEnv()
  );

  assert.equal(authentication.ok, true);

  const target = createWorkableTargetUrl(
    new Request("https://admin.example.com/api/workable/workers/worker-1/actions/cancel"),
    ["workers", "worker-1", "actions", "cancel"],
    secureEnv()
  );

  assert.equal(target.ok, true);
  if (!target.ok) {
    return;
  }

  assert.equal(
    target.url.toString(),
    "https://workable.example.com/workable/workers/worker-1/actions/cancel"
  );
});

test("proxy preserves hosted Workable API authorization failures", async () => {
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/workers/worker-1/actions/cancel", {
      method: "POST",
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
        "content-type": "application/json",
        origin: "https://admin.example.com",
      },
      body: "{}",
    }),
    ["workers", "worker-1", "actions", "cancel"],
    {
      env: secureEnv(),
      fetch: async () =>
        new Response(JSON.stringify({ error: "denied by hosted Workable API" }), {
          status: 403,
          headers: {
            "content-type": "application/json",
          },
        }),
    }
  );

  assert.equal(response.status, 403);
  assert.deepEqual(await response.json(), {
    error: "denied by hosted Workable API",
  });
});

test("proxy surfaces hosted issuer mismatch hints from bearer challenges", async () => {
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
    }),
    ["host"],
    {
      env: secureEnv(),
      fetch: async () =>
        new Response(JSON.stringify({ error: "Unauthorized" }), {
          status: 401,
          headers: {
            "content-type": "application/json",
            "www-authenticate": 'Bearer error="invalid_token", error_description="IDX10205: Issuer validation failed."',
          },
        }),
    }
  );

  assert.equal(response.status, 401);
  assert.deepEqual(await response.json(), {
    error:
      "The hosted Workable API rejected the bearer token because the token issuer does not match its Entra configuration. Check that the target API app registration is configured to issue v2 access tokens.",
  });
});

test("proxy surfaces hosted audience mismatch hints from bearer challenges", async () => {
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
    }),
    ["host"],
    {
      env: secureEnv(),
      fetch: async () =>
        new Response(JSON.stringify({ error: "Unauthorized" }), {
          status: 401,
          headers: {
            "content-type": "application/json",
            "www-authenticate": 'Bearer error="invalid_token", error_description="IDX10214: Audience validation failed."',
          },
        }),
    }
  );

  assert.equal(response.status, 401);
  assert.deepEqual(await response.json(), {
    error:
      "The hosted Workable API rejected the bearer token because the token audience does not match its Entra configuration. Check that the admin UI target scope and the hosted API accepted audiences refer to the same app registration.",
  });
});

test("proxy responses are not cacheable and do not serve hosted HTML as admin HTML", async () => {
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
    }),
    ["host"],
    {
      env: secureEnv(),
      fetch: async () =>
        new Response("<script>alert(1)</script>", {
          status: 200,
          headers: {
            "content-type": "text/html; charset=utf-8",
          },
        }),
    }
  );

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.equal(response.headers.get("content-type"), "text/plain; charset=utf-8");
  assert.equal(await response.text(), "<script>alert(1)</script>");
});

test("proxy forwards the configured Entra target API token to the configured host", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
    ]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(env, new Request("https://admin.example.com/"));
  let authorizationHeader: string | null | undefined;

  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        cookie: cookieHeader,
      },
    }),
    ["host"],
    {
      env,
      fetch: async (_url, init) => {
        authorizationHeader = new Headers(init?.headers).get("authorization");
        return new Response(JSON.stringify({ capabilities: { realtime: { enabled: false, transport: null, hubPath: null } }, systems: [] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(authorizationHeader, "Bearer hosted-api-access-token");
});

test("proxy does not forward the configured Entra token to a different allowed host", async () => {
  const env = entraEnv({
    WORKABLE_ALLOWED_API_URLS: "https://ops.example.com/workable",
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
    ]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(env, new Request("https://admin.example.com/"));
  let authorizationHeader: string | null | undefined;

  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        cookie: cookieHeader,
        "x-workable-api-url": "https://ops.example.com/workable",
      },
    }),
    ["host"],
    {
      env,
      fetch: async (_url, init) => {
        authorizationHeader = new Headers(init?.headers).get("authorization");
        return new Response(JSON.stringify({ capabilities: { realtime: { enabled: false, transport: null, hubPath: null } }, systems: [] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(authorizationHeader, null);
});

test("proxy refreshes an expired Entra target API token before forwarding", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
    ]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      access_token: "expired-access-token",
      expires_in: 1,
      refresh_token: "refresh-me",
      token_type: "Bearer",
    }
  );
  const requestedUrls: string[] = [];
  let authorizationHeader: string | null | undefined;

  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        cookie: cookieHeader,
      },
    }),
    ["host"],
    {
      env,
      fetch: async (url, init) => {
        requestedUrls.push(String(url));
        if (String(url).includes(".well-known/openid-configuration")) {
          return new Response(JSON.stringify({
            token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }

        if (String(url).endsWith("/oauth2/v2.0/token")) {
          const body = init?.body instanceof URLSearchParams
            ? init.body.toString()
            : String(init?.body ?? "");
          assert.match(body, /grant_type=refresh_token/);
          assert.match(body, /scope=api%3A%2F%2Factually-client-id%2Fworkable.access/);
          return new Response(JSON.stringify({
            access_token: "refreshed-access-token",
            expires_in: 3600,
            refresh_token: "refreshed-refresh-token",
            token_type: "Bearer",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }

        authorizationHeader = new Headers(init?.headers).get("authorization");
        return new Response(JSON.stringify({ capabilities: { realtime: { enabled: false, transport: null, hubPath: null } }, systems: [] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(authorizationHeader, "Bearer refreshed-access-token");
  assert.ok(requestedUrls.some((url) => url.includes(".well-known/openid-configuration")));
  assert.ok(requestedUrls.some((url) => url.endsWith("/oauth2/v2.0/token")));
  assert.ok(
    getSetCookies(response.headers).some((cookie) =>
      cookie.startsWith("workable_admin_entra_target_token.parts=")
    )
  );
});

test("proxy refreshes and forwards the correct token for each configured hosted API", async () => {
  const env = entraEnv({
    WORKABLE_ALLOWED_API_URLS: "https://ops.example.com/workable",
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
      {
        apiUrl: "https://ops.example.com/workable",
        scope: "api://ops-client-id/workable.access",
      },
    ]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      refresh_token: "refresh-me",
    }
  );
  const tokenBodies: string[] = [];
  let authorizationHeader: string | null | undefined;

  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        cookie: cookieHeader,
        "x-workable-api-url": "https://ops.example.com/workable",
      },
    }),
    ["host"],
    {
      env,
      fetch: async (url, init) => {
        if (String(url).includes(".well-known/openid-configuration")) {
          return new Response(JSON.stringify({
            token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }

        if (String(url).endsWith("/oauth2/v2.0/token")) {
          const body = init?.body instanceof URLSearchParams
            ? init.body.toString()
            : String(init?.body ?? "");
          tokenBodies.push(body);
          return new Response(JSON.stringify({
            access_token: "ops-access-token",
            expires_in: 3600,
            refresh_token: "ops-refresh-token",
            token_type: "Bearer",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }

        authorizationHeader = new Headers(init?.headers).get("authorization");
        return new Response(JSON.stringify({ capabilities: { realtime: { enabled: false, transport: null, hubPath: null } }, systems: [] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(authorizationHeader, "Bearer ops-access-token");
  assert.equal(tokenBodies.length, 1);
  assert.match(tokenBodies[0] ?? "", /scope=api%3A%2F%2Fops-client-id%2Fworkable.access/);
});

test("proxy binds Entra target token refresh to the actual proxied host", async () => {
  const env = entraEnv({
    WORKABLE_ALLOWED_API_URLS: "https://ops.example.com/workable",
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
      {
        apiUrl: "https://ops.example.com/workable",
        scope: "api://ops-client-id/workable.access",
      },
    ]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      refresh_token: "refresh-me",
    }
  );
  const tokenBodies: string[] = [];
  let proxiedUrl: string | null = null;

  const response = await proxyWorkableRequest(
    new Request(
      "https://admin.example.com/api/workable/host?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
      {
        headers: {
          cookie: cookieHeader,
          "x-workable-api-url": "https://ops.example.com/workable",
        },
      }
    ),
    ["host"],
    {
      env,
      fetch: async (url, init) => {
        if (String(url).includes(".well-known/openid-configuration")) {
          return new Response(JSON.stringify({
            token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }

        if (String(url).endsWith("/oauth2/v2.0/token")) {
          const body = init?.body instanceof URLSearchParams
            ? init.body.toString()
            : String(init?.body ?? "");
          tokenBodies.push(body);
          return new Response(JSON.stringify({
            access_token: "ops-access-token",
            expires_in: 3600,
            refresh_token: "ops-refresh-token",
            token_type: "Bearer",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }

        proxiedUrl = String(url);
        return new Response(JSON.stringify({ capabilities: { realtime: { enabled: false, transport: null, hubPath: null } }, systems: [] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(proxiedUrl, "https://ops.example.com/workable/host?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable");
  assert.equal(tokenBodies.length, 1);
  assert.match(tokenBodies[0] ?? "", /scope=api%3A%2F%2Fops-client-id%2Fworkable.access/);
});

test("oversized Entra target token state expires cookies instead of writing unreadable chunks", () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
    ]),
  });

  const cookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "a".repeat(80_000),
      expires_in: 3600,
      refresh_token: "b".repeat(80_000),
      token_type: "Bearer",
    },
    new Request("https://admin.example.com/"),
    getAdminSecuritySettings(env)
  );

  assert.deepEqual(cookies, createExpiredEntraTargetTokenCookies());
});

test("proxy explains trusted certificate requirements for local HTTPS loopback failures", async () => {
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
    }),
    ["host"],
    {
      env: secureEnv({
        NODE_ENV: "development",
        WORKABLE_API_URL: "https://localhost:7058/workable",
      }),
      fetch: async () => {
        throw new Error("fetch failed");
      },
    }
  );

  assert.equal(response.status, 502);
  assert.deepEqual(await response.json(), {
    error:
      "Unable to reach the Workable HTTP API. Local HTTPS loopback hosts must present a trusted development certificate to the admin UI proxy.",
  });
});

test("unsafe admin API requests require a same-origin Origin header", () => {
  const missingOrigin = validateUnsafeRequestOrigin(
    new Request("https://admin.example.com/api/workable/work/example", {
      method: "POST",
    })
  );
  const crossOrigin = validateUnsafeRequestOrigin(
    new Request("https://admin.example.com/api/workable/work/example", {
      method: "POST",
      headers: {
        origin: "https://evil.example.com",
      },
    })
  );
  const sameOrigin = validateUnsafeRequestOrigin(
    new Request("https://admin.example.com/api/workable/work/example", {
      method: "POST",
      headers: {
        origin: "https://admin.example.com",
      },
    })
  );

  assert.equal(missingOrigin.ok, false);
  assert.equal(crossOrigin.ok, false);
  assert.equal(sameOrigin.ok, true);
});

test("production proxy target fails closed when WORKABLE_API_URL is missing", () => {
  const target = createWorkableTargetUrl(
    new Request("https://admin.example.com/api/workable/host"),
    ["host"],
    {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_DISABLED: "true",
    }
  );

  assert.equal(target.ok, false);
});

test("server-only local config file can supply credentials without operation roles", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-"));
  const configPath = join(temp, "workable-admin.config.local.json");
  writeFileSync(
    configPath,
    JSON.stringify({
      apiUrl: "https://workable.example.com/workable",
      basicAuth: {
        username: "admin",
        password: "correct horse battery staple",
      },
      sessionSecret: "replace-with-a-different-long-random-secret",
    })
  );

  try {
    const env = {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_PATH: configPath,
    };
    const authentication = authenticateAdminRequest(
      new Headers({ authorization: basic("admin", "correct horse battery staple") }),
      env
    );

    assert.equal(authentication.ok, true);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test("browser-supplied Workable API URLs must be allow-listed", () => {
  const target = createWorkableTargetUrl(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        "x-workable-api-url": "https://evil.example.com/workable",
      },
    }),
    ["host"],
    secureEnv()
  );

  assert.equal(target.ok, false);
});

test("hostile realtime hub metadata cannot switch origins or protocols", () => {
  assert.equal(
    createWorkableRealtimeUrl({
      apiUrl: "https://workable.example.com/workable",
      realtimeHubPath: "javascript:alert(1)",
    }),
    null
  );
  assert.equal(
    createWorkableRealtimeUrl({
      apiUrl: "https://workable.example.com/workable",
      realtimeHubPath: "https://evil.example.com/workable/realtime",
    }),
    null
  );
  assert.equal(
    createWorkableRealtimeUrl({
      apiUrl: "https://workable.example.com/workable",
      realtimeHubPath: "/workable/realtime",
    }),
    "https://workable.example.com/workable/realtime"
  );
});

function secureEnv(overrides: AdminSecurityEnvironment = {}): AdminSecurityEnvironment {
  return {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
    WORKABLE_API_URL: "https://workable.example.com/workable",
    WORKABLE_ADMIN_UI_USERNAME: "admin",
    WORKABLE_ADMIN_UI_PASSWORD: "correct horse battery staple",
    ...overrides,
  };
}

function entraEnv(overrides: AdminSecurityEnvironment = {}): AdminSecurityEnvironment {
  return {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
    WORKABLE_ADMIN_UI_AUTH_PROVIDER: "entra",
    WORKABLE_API_URL: "https://workable.example.com/workable",
    WORKABLE_ADMIN_ENTRA_TENANT_ID: "tenant-id",
    WORKABLE_ADMIN_ENTRA_CLIENT_ID: "client-id",
    WORKABLE_ADMIN_UI_SESSION_SECRET: "replace-with-a-different-long-random-secret",
    ...overrides,
  };
}

function basic(userName: string, password: string) {
  return `Basic ${Buffer.from(`${userName}:${password}`).toString("base64")}`;
}

function getSetCookies(headers: Headers) {
  const extended = headers as Headers & {
    getSetCookie?: () => string[];
  };
  return extended.getSetCookie?.() ?? [headers.get("set-cookie") ?? ""];
}

function createEntraAuthenticatedCookieHeader(
  env: AdminSecurityEnvironment,
  request: Request,
  tokens: {
    access_token?: string;
    expires_in?: number;
    refresh_token?: string;
    token_type?: string;
  } = {
    access_token: "hosted-api-access-token",
    expires_in: 3600,
    refresh_token: "hosted-api-refresh-token",
    token_type: "Bearer",
  }
) {
  const sessionCookie = createAdminSessionCookie("admin", request, env, "entra");
  assert.equal(sessionCookie.ok, true);
  if (!sessionCookie.ok) {
    throw new Error("Expected Entra session cookie.");
  }

  const tokenCookies = createEntraTargetTokenCookieHeaders(
    tokens,
    request,
    getAdminSecuritySettings(env)
  );

  return [
    sessionCookie.header,
    ...tokenCookies,
  ]
    .map((header) => header.split(";")[0] ?? "")
    .filter(Boolean)
    .join("; ");
}
