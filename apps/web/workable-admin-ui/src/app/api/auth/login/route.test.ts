import assert from "node:assert/strict";
import test from "node:test";
import { POST } from "./route.ts";
import { resetBasicAuthenticationAttemptsForTests } from "@/lib/admin-security/basic";
import {
  authenticateAdminRequest,
  createAdminLogoutTombstoneCookies,
} from "@/lib/admin-security";

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
    assert.equal(response.headers.get("cache-control"), "no-store");
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
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
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

test("a delayed Basic login response cannot remove a newer logout tombstone", async () => {
  await withLoginRouteEnv(async () => {
    const firstLogout = createAdminLogoutTombstoneCookie(
      new Request("https://admin.example.com/api/auth/logout")
    );
    let releaseBody!: () => void;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        releaseBody = () => {
          controller.enqueue(new TextEncoder().encode(JSON.stringify({
            userName: "admin",
            password: "secret",
          })));
          controller.close();
        };
      },
    });
    const delayedLogin = POST(new Request("https://admin.example.com/api/auth/login", {
      body,
      headers: {
        "content-type": "application/json",
        cookie: firstLogout.split(";")[0] ?? "",
        origin: "https://admin.example.com",
      },
      method: "POST",
      duplex: "half",
    } as RequestInit & { duplex: "half" }));
    const currentLogout = createAdminLogoutTombstoneCookie(
      new Request("https://admin.example.com/api/auth/logout", {
        headers: { cookie: firstLogout.split(";")[0] ?? "" },
      })
    ).split(";")[0] ?? "";
    releaseBody();

    const response = await delayedLogin;
    const cookies = getSetCookies(response.headers);
    const delayedSession = cookies.find((cookie) =>
      /workable_admin_session=/.test(cookie)
    )?.split(";")[0] ?? "";

    assert.equal(response.status, 200);
    assert.ok(delayedSession);
    assert.equal(cookies.some((cookie) =>
      cookie.startsWith("__Host-workable_admin_logout_")
    ), false);

    const authentication = authenticateAdminRequest(
      new Headers({ cookie: `${delayedSession}; ${currentLogout}` }),
      process.env
    );
    assert.equal(authentication.ok, false);
    assert.equal(authentication.status, 401);
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
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.deepEqual(await response.json(), {
      error: "The username or password is not valid.",
    });
    assert.equal(response.headers.get("set-cookie"), null);
  });
});

test("login route rejects repeated credential guesses", async () => {
  await withLoginRouteEnv(async () => {
    let response: Response | null = null;
    for (let attempt = 0; attempt < 5; attempt++) {
      response = await POST(new Request("https://admin.example.com/api/auth/login", {
        body: JSON.stringify({ userName: "admin", password: `wrong-${attempt}` }),
        headers: {
          "content-type": "application/json",
          origin: "https://admin.example.com",
          "x-forwarded-for": "192.0.2.10",
        },
        method: "POST",
      }));
    }

    assert.equal(response?.status, 429);
    assert.equal(response?.headers.get("retry-after"), "60");
    assert.deepEqual(await response?.json(), {
      error: "Too many failed Basic authentication attempts. Try again later.",
    });

    const otherSource = await POST(new Request("https://admin.example.com/api/auth/login", {
      body: JSON.stringify({ userName: "admin", password: "secret" }),
      headers: {
        "content-type": "application/json",
        origin: "https://admin.example.com",
        "x-forwarded-for": "192.0.2.11",
      },
      method: "POST",
    }));
    assert.equal(otherSource.status, 200);
  });
});

test("login route rejects and cancels an oversized credential body", async () => {
  await withLoginRouteEnv(async () => {
    let cancelled = false;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode("x".repeat(16 * 1024 + 1)));
      },
      cancel() {
        cancelled = true;
        throw new Error("request cancellation failed");
      },
    });
    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      body,
      headers: {
        "content-type": "application/json",
        origin: "https://admin.example.com",
      },
      method: "POST",
      duplex: "half",
    } as RequestInit & { duplex: "half" }));

    assert.equal(response.status, 413);
    assert.equal(cancelled, true);
    assert.deepEqual(await response.json(), {
      error: "The login request body is too large.",
    });
  });
});

test("login route rejects an empty credential body", async () => {
  await withLoginRouteEnv(async () => {
    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      headers: {
        "content-type": "application/json",
        origin: "https://admin.example.com",
      },
      method: "POST",
    }));

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), {
      error: "Username and password are required.",
    });
  });
});

test("login route treats interrupted credential streams as invalid input", async () => {
  await withLoginRouteEnv(async () => {
    const body = new ReadableStream<Uint8Array>({
      pull() {
        throw new Error("client disconnected");
      },
    });
    const response = await POST(new Request("https://admin.example.com/api/auth/login", {
      body,
      headers: {
        "content-type": "application/json",
        origin: "https://admin.example.com",
      },
      method: "POST",
      duplex: "half",
    } as RequestInit & { duplex: "half" }));

    assert.equal(response.status, 400);
    assert.deepEqual(await response.json(), {
      error: "Username and password are required.",
    });
  });
});

async function withLoginRouteEnv<T>(callback: () => Promise<T> | T): Promise<T> {
  const previous = snapshotEnv();
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_ADMIN_UI_USERNAME = "admin";
  process.env.WORKABLE_ADMIN_UI_PASSWORD = "secret";
  process.env.WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED = "true";
  process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = "login-route-test-session-secret-at-least-32-bytes";
  delete process.env.WORKABLE_ADMIN_UI_AUTH_PROVIDER;
  resetBasicAuthenticationAttemptsForTests();

  try {
    return await callback();
  } finally {
    resetBasicAuthenticationAttemptsForTests();
    restoreEnv(previous);
  }
}

function snapshotEnv() {
  return {
    WORKABLE_ADMIN_CONFIG_DISABLED: process.env.WORKABLE_ADMIN_CONFIG_DISABLED,
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

function getSetCookies(headers: Headers) {
  const extended = headers as Headers & { getSetCookie?: () => string[] };
  return extended.getSetCookie?.() ?? [headers.get("set-cookie") ?? ""];
}

function createAdminLogoutTombstoneCookie(request: Request) {
  return createAdminLogoutTombstoneCookies(request)[0] ?? "";
}
