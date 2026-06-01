import assert from "node:assert/strict";
import test from "node:test";
import { POST } from "./route.ts";

test("login route rejects unsafe POST requests without a same-origin Origin", async () => {
  await withLoginRouteEnv(async () => {
    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      body: JSON.stringify({ userName: "admin", password: "secret" }),
      headers: {
        "content-type": "application/json",
      },
      method: "POST",
    }));

    assert.equal(response.status, 403);
    assert.deepEqual(await response.json(), {
      error: "Unsafe Workable admin UI requests require a same-origin Origin header.",
    });
  });
});

test("login route accepts JSON credentials and writes an admin session cookie", async () => {
  await withLoginRouteEnv(async () => {
    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      body: JSON.stringify({ userName: "admin", password: "secret" }),
      headers: {
        "content-type": "application/json",
        origin: "https://admin.example.com",
      },
      method: "POST",
    }));

    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { userName: "admin" });
    assert.match(response.headers.get("set-cookie") ?? "", /workable_admin_session=/);
    assert.match(response.headers.get("set-cookie") ?? "", /HttpOnly/);
  });
});

test("login route accepts browser form credentials", async () => {
  await withLoginRouteEnv(async () => {
    const body = new FormData();
    body.set("userName", "admin");
    body.set("password", "secret");

    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      body,
      headers: {
        origin: "https://admin.example.com",
      },
      method: "POST",
    }));

    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { userName: "admin" });
  });
});

test("login route reports bad credentials without creating a session", async () => {
  await withLoginRouteEnv(async () => {
    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      body: JSON.stringify({ userName: "admin", password: "wrong" }),
      headers: {
        "content-type": "application/json",
        origin: "https://admin.example.com",
      },
      method: "POST",
    }));

    assert.equal(response.status, 401);
    assert.deepEqual(await response.json(), {
      error: "The username or password is not valid.",
    });
    assert.equal(response.headers.get("set-cookie"), null);
  });
});

async function withLoginRouteEnv<T>(callback: () => Promise<T> | T): Promise<T> {
  const previous = snapshotEnv();
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_ADMIN_UI_USERNAME = "admin";
  process.env.WORKABLE_ADMIN_UI_PASSWORD = "secret";
  process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = "login-route-test-session-secret";
  delete process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER;

  try {
    return await callback();
  } finally {
    restoreEnv(previous);
  }
}

function snapshotEnv() {
  return {
    WORKABLE_ADMIN_CONFIG_DISABLED: process.env.WORKABLE_ADMIN_CONFIG_DISABLED,
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
