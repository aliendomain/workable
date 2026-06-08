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

function withProxyEnv<T>(callback: () => T): T {
  const previous = snapshotEnv();
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_ADMIN_UI_USERNAME = "admin";
  process.env.WORKABLE_ADMIN_UI_PASSWORD = "secret";
  process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = "proxy-test-session-secret";
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
