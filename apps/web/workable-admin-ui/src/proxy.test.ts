import assert from "node:assert/strict";
import test from "node:test";
import { NextRequest } from "next/server";
import { proxy } from "./proxy.ts";

test("proxy lets public admin routes pass without authentication", () => {
  withProxyEnv(() => {
    const response = proxy(new NextRequest("https://admin.example.com/login"));

    assert.equal(response.headers.get("x-middleware-next"), "1");
  });
});

test("proxy redirects unauthenticated page requests to login with the original path", async () => {
  await withProxyEnv(async () => {
    const response = proxy(
      new NextRequest("https://admin.example.com/workers?state=Running")
    );

    assert.equal(response.status, 307);
    assert.equal(
      response.headers.get("location"),
      "https://admin.example.com/login?next=%2Fworkers%3Fstate%3DRunning"
    );
  });
});

test("proxy returns JSON authentication failures for API requests", async () => {
  await withProxyEnv(async () => {
    const response = proxy(
      new NextRequest("https://admin.example.com/api/workable/views/overview")
    );

    assert.equal(response.status, 401);
    assert.deepEqual(await response.json(), {
      error: "Authentication is required for the Workable admin UI.",
    });
  });
});

test("proxy passes authenticated Basic requests through to the app", () => {
  withProxyEnv(() => {
    const authorization = `Basic ${Buffer.from("admin:secret").toString("base64")}`;
    const response = proxy(
      new NextRequest("https://admin.example.com/", {
        headers: {
          authorization,
        },
      })
    );

    assert.equal(response.headers.get("x-middleware-next"), "1");
    assert.equal(response.headers.has("location"), false);
  });
});

test("proxy clears the session and delegated tokens for an invalid Entra session", () => {
  withProxyEnv(() => {
    process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER = "entra";
    process.env.WORKABLE_ADMIN_ENTRA_TENANT_ID = "tenant-id";
    process.env.WORKABLE_ADMIN_ENTRA_CLIENT_ID = "client-id";
    const response = proxy(
      new NextRequest("https://admin.example.com/workers", {
        headers: { cookie: "workable_admin_session=invalid" },
      })
    );
    const cookies = getSetCookies(response.headers);

    assert.equal(response.status, 307);
    assert.equal(cookies.length, 35);
    assert.ok(cookies.every((cookie) => /Max-Age=0/i.test(cookie)));
  });
});

function getSetCookies(headers: Headers) {
  const extended = headers as Headers & { getSetCookie?: () => string[] };
  return extended.getSetCookie?.() ?? [headers.get("set-cookie") ?? ""];
}

function withProxyEnv<T>(callback: () => T): T {
  const previous = snapshotEnv();
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_ADMIN_UI_USERNAME = "admin";
  process.env.WORKABLE_ADMIN_UI_PASSWORD = "secret";
  process.env.WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED = "true";
  process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = "proxy-test-session-secret-at-least-32-bytes";
  delete process.env.WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS;
  delete process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER;

  try {
    return callback();
  } finally {
    restoreEnv(previous);
  }
}

function snapshotEnv() {
  return {
    WORKABLE_ADMIN_CONFIG_DISABLED: process.env.WORKABLE_ADMIN_CONFIG_DISABLED,
    WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS: process.env.WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS,
    WORKABLE_ADMIN_UI_AUTH_PROVIDER: process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER,
    WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: process.env.WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED,
    WORKABLE_ADMIN_ENTRA_CLIENT_ID: process.env.WORKABLE_ADMIN_ENTRA_CLIENT_ID,
    WORKABLE_ADMIN_ENTRA_TENANT_ID: process.env.WORKABLE_ADMIN_ENTRA_TENANT_ID,
    WORKABLE_ADMIN_UI_PASSWORD: process.env.WORKABLE_ADMIN_UI_PASSWORD,
    WORKABLE_ADMIN_UI_SESSION_SECRET: process.env.WORKABLE_ADMIN_UI_SESSION_SECRET,
    WORKABLE_ADMIN_UI_USERNAME: process.env.WORKABLE_ADMIN_UI_USERNAME,
  };
}

function restoreEnv(previous: ReturnType<typeof snapshotEnv>) {
  for (const [key, value] of Object.entries(previous)) {
    if (value === undefined) {
      delete process.env[key];
    } else {
      process.env[key] = value;
    }
  }
}
