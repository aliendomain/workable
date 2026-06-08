import assert from "node:assert/strict";
import test from "node:test";
import { GET } from "./route.ts";

test("workable API route enforces admin authentication without relying on proxy", async () => {
  await withWorkableRouteEnv(async () => {
    const response = await GET(
      new Request("https://admin.example.com/api/workable/host"),
      routeContext("host")
    );

    assert.equal(response.status, 401);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.deepEqual(await response.json(), {
      error: "Authentication is required for the Workable admin UI.",
    });
  });
});

test("workable API route rejects browser-supplied targets outside the allowlist", async () => {
  await withWorkableRouteEnv(async () => {
    const response = await GET(
      new Request("https://admin.example.com/api/workable/host", {
        headers: {
          authorization: basic("admin", "secret"),
          "x-workable-api-url": "https://evil.example.com/workable",
        },
      }),
      routeContext("host")
    );

    assert.equal(response.status, 400);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.deepEqual(await response.json(), {
      error:
        "Workable API URL is not allowed. Configure WORKABLE_API_URL, WORKABLE_ALLOWED_API_URLS, apiUrl, or allowedApiUrls.",
    });
  });
});

function routeContext(...path: string[]) {
  return {
    params: Promise.resolve({ path }),
  };
}

async function withWorkableRouteEnv<T>(callback: () => Promise<T> | T): Promise<T> {
  const previous = snapshotEnv();
  const env = process.env as Record<string, string | undefined>;
  env.NODE_ENV = "production";
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_API_URL = "https://workable.example.com/workable";
  process.env.WORKABLE_ADMIN_UI_USERNAME = "admin";
  process.env.WORKABLE_ADMIN_UI_PASSWORD = "secret";
  process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = "workable-route-test-session-secret";
  delete process.env.WORKABLE_ALLOWED_API_URLS;
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
    WORKABLE_ALLOWED_API_URLS: process.env.WORKABLE_ALLOWED_API_URLS,
    WORKABLE_API_URL: process.env.WORKABLE_API_URL,
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

function basic(userName: string, password: string) {
  return `Basic ${Buffer.from(`${userName}:${password}`).toString("base64")}`;
}
