import assert from "node:assert/strict";
import test from "node:test";
import { GET } from "./route.ts";

test("workable token route enforces admin authentication without relying on proxy", async () => {
  await withTokenRouteEnv(async () => {
    const response = await GET(
      new Request("https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable")
    );

    assert.equal(response.status, 401);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.deepEqual(await response.json(), {
      error: "Authentication is required for the Workable admin UI.",
    });
  });
});

test("workable token route does not expose refresh state when no target binding exists", async () => {
  await withTokenRouteEnv(async () => {
    const response = await GET(
      new Request("https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable", {
        headers: {
          authorization: basic("admin", "secret"),
        },
      })
    );

    assert.equal(response.status, 200);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.equal(response.headers.get("set-cookie"), null);
    assert.deepEqual(await response.json(), {
      accessToken: null,
    });
  });
});

async function withTokenRouteEnv<T>(callback: () => Promise<T> | T): Promise<T> {
  const previous = snapshotEnv();
  const env = process.env as Record<string, string | undefined>;
  env.NODE_ENV = "production";
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_API_URL = "https://workable.example.com/workable";
  process.env.WORKABLE_ADMIN_UI_USERNAME = "admin";
  process.env.WORKABLE_ADMIN_UI_PASSWORD = "secret";
  process.env.WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED = "true";
  process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = "workable-token-route-test-session-secret";
  delete process.env.WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON;
  delete process.env.WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS;
  delete process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER;

  try {
    return await callback();
  } finally {
    restoreEnv(previous);
  }
}

function snapshotEnv() {
  return {
    NODE_ENV: process.env.NODE_ENV,
    WORKABLE_ADMIN_CONFIG_DISABLED: process.env.WORKABLE_ADMIN_CONFIG_DISABLED,
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: process.env.WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON,
    WORKABLE_API_URL: process.env.WORKABLE_API_URL,
    WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS: process.env.WORKABLE_ADMIN_UI_ALLOW_ANONYMOUS,
    WORKABLE_ADMIN_UI_AUTH_PROVIDER: process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER,
    WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: process.env.WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED,
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

function basic(userName: string, password: string) {
  return `Basic ${Buffer.from(`${userName}:${password}`).toString("base64")}`;
}
