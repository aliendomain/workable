import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import test from "node:test";
import { POST } from "./route.ts";

const SESSION_SECRET = "logout-route-test-session-secret-at-least-32-bytes";

test("logout route rejects unsafe POST requests without a same-origin Origin", async () => {
  const response = await POST(new Request("https://admin.example.com/api/auth/logout", {
    method: "POST",
  }));

  assert.equal(response.status, 403);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.deepEqual(await response.json(), {
    error: "Unsafe Workable admin UI requests require a same-origin Origin header.",
  });
});

test("logout route creates a signed logout barrier when session signing is configured", async () => {
  await withLogoutRouteEnv(SESSION_SECRET, async () => {
    const response = await sameOriginLogout();
    const cookies = getSetCookies(response.headers);
    const activeCookies = cookies.filter((cookie) => !/Max-Age=0(?:;|$)/i.test(cookie));

    assertSuccessfulLogout(response);
    assert.equal(activeCookies.length, 1);
    const barrier = /^__Host-workable_admin_logout_([0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})=([A-Za-z0-9_-]{43});/.exec(
      activeCookies[0] ?? ""
    );
    assert.ok(barrier);
    assert.equal(
      barrier[2],
      createHmac("sha256", SESSION_SECRET)
        .update(`workable.admin.logout.tombstone.v1:${barrier[1]}`)
        .digest("base64url")
    );
    assert.match(activeCookies[0] ?? "", /; Path=\/;/);
    assert.match(activeCookies[0] ?? "", /; SameSite=Lax;/);
    assert.match(activeCookies[0] ?? "", /; HttpOnly;/);
    assert.match(activeCookies[0] ?? "", /; Secure(?:;|$)/);
    assert.ok(cookies.some((cookie) =>
      /^__Host-workable_admin_session=;/.test(cookie) && /Max-Age=0/i.test(cookie)
    ));
    assert.ok(cookies.some((cookie) =>
      /^__Host-workable_admin_entra_state=;/.test(cookie) && /Max-Age=0/i.test(cookie)
    ));
  });
});

test("logout route emits only cleanup cookies when session signing is not configured", async () => {
  await withLogoutRouteEnv(undefined, async () => {
    const response = await sameOriginLogout();
    const cookies = getSetCookies(response.headers);

    assertSuccessfulLogout(response);
    assert.ok(cookies.length > 0);
    assert.ok(cookies.every((cookie) => /Max-Age=0(?:;|$)/i.test(cookie)));
    assert.ok(cookies.some((cookie) =>
      /^__Host-workable_admin_session=;/.test(cookie)
    ));
    assert.equal(cookies.some((cookie) => /workable_admin_logout_/i.test(cookie)), false);
  });
});

function sameOriginLogout() {
  return POST(new Request("https://admin.example.com/api/auth/logout", {
    headers: {
      origin: "https://admin.example.com",
    },
    method: "POST",
  }));
}

async function assertSuccessfulLogout(response: Response) {
  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.deepEqual(await response.json(), { ok: true });
}

async function withLogoutRouteEnv<T>(
  sessionSecret: string | undefined,
  callback: () => Promise<T>
): Promise<T> {
  const previous = snapshotEnv();
  process.env.WORKABLE_ADMIN_CONFIG_DISABLED = "true";
  process.env.WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS = "86400";
  process.env.WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME = "__Host-workable_admin_session";
  if (sessionSecret === undefined) {
    delete process.env.WORKABLE_ADMIN_UI_SESSION_SECRET;
  } else {
    process.env.WORKABLE_ADMIN_UI_SESSION_SECRET = sessionSecret;
  }

  try {
    return await callback();
  } finally {
    restoreEnv(previous);
  }
}

function snapshotEnv() {
  return {
    WORKABLE_ADMIN_CONFIG_DISABLED: process.env.WORKABLE_ADMIN_CONFIG_DISABLED,
    WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS:
      process.env.WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS,
    WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME:
      process.env.WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME,
    WORKABLE_ADMIN_UI_SESSION_SECRET: process.env.WORKABLE_ADMIN_UI_SESSION_SECRET,
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
