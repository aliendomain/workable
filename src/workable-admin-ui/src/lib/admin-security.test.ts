import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  authenticateAdminRequest,
  createEntraAuthorizationResponse,
  createAdminSessionCookie,
  createWorkableTargetUrl,
  getAdminAuthProvider,
  validateUnsafeRequestOrigin,
  type AdminSecurityEnvironment,
} from "./admin-security.ts";
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

test("proxy explains trusted certificate requirements for local HTTPS loopback failures", async () => {
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/systems", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
    }),
    ["systems"],
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
    new Request("https://admin.example.com/api/workable/systems"),
    ["systems"],
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
    new Request("https://admin.example.com/api/workable/systems", {
      headers: {
        "x-workable-api-url": "https://evil.example.com/workable",
      },
    }),
    ["systems"],
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
