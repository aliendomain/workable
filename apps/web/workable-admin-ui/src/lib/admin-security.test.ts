import assert from "node:assert/strict";
import { generateKeyPairSync, sign } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import {
  authenticateAdminRequest,
  completeEntraLogin,
  createEntraAuthorizationResponse,
  createEntraTargetAccessTokenResponse,
  createAdminLogoutTombstoneCookie,
  createAdminSessionCookie,
  createExpiredAdminSessionCookie,
  createWorkableTargetUrl,
  getAdminAuthProvider,
  validateUnsafeRequestOrigin,
  verifyAdminCredentials,
  type AdminSecurityEnvironment,
} from "./admin-security.ts";
import { getAdminSecuritySettings } from "./admin-security/config.ts";
import {
  createExpiredEntraTargetTokenCookies,
  createEntraTargetTokenCookieHeaders,
  resetEntraRefreshCoordinatorsForTests,
} from "./admin-security/entra-downstream.ts";
import { normalizeAdminReturnPath } from "./admin-security/return-path.ts";
import { sign as signAdminValue } from "./admin-security/crypto.ts";
import { createSignedAdminSessionCookie } from "./admin-security/session.ts";
import {
  authenticateBasicRequest,
  basicAuthenticationAttemptBucketCountForTests,
  resetBasicAuthenticationAttemptsForTests,
} from "./admin-security/basic.ts";
import {
  fetchCachedEntraJson,
  fetchEntraJson,
  MAXIMUM_ENTRA_JSON_BYTES,
  resetEntraBackchannelCachesForTests,
  validateEntraBackchannelUrl,
} from "./admin-security/entra-backchannel.ts";
import { createWorkableRealtimeUrl } from "./workable.ts";
import { proxyWorkableRequest } from "./workable-proxy.ts";

const TEST_ENTRA_SUBJECT = JSON.stringify([
  "workable.entra.subject.v1",
  "oid",
  "tenant-id",
  "actor-id",
]);

test("admin UI authentication fails closed when credentials are not configured", () => {
  const result = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
  });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
});

test("Basic authentication remains disabled until explicitly enabled", () => {
  const result = authenticateAdminRequest(
    new Headers({ authorization: basic("admin", "correct horse battery staple") }),
    secureEnv({ WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: undefined })
  );

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
  assert.match(result.error, /disabled/i);

  const verification = verifyAdminCredentials(
    "admin",
    "correct horse battery staple",
    secureEnv({ WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: undefined })
  );
  assert.equal(verification.ok, false);
  assert.equal(verification.status, 503);

  const cookie = createAdminSessionCookie(
    "admin",
    new Request("https://admin.example.com/api/auth/login", { method: "POST" }),
    secureEnv({ WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: undefined })
  );
  assert.equal(cookie.ok, false);
  assert.equal(cookie.status, 503);
});

test("disabling Basic invalidates an existing Basic session", () => {
  const enabled = secureEnv();
  const cookie = createAdminSessionCookie(
    "admin",
    new Request("https://admin.example.com/api/auth/login", { method: "POST" }),
    enabled
  );
  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const disabled = authenticateAdminRequest(
    new Headers({ cookie: cookie.header.split(";")[0] ?? "" }),
    secureEnv({ WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: undefined })
  );
  assert.equal(disabled.ok, false);
  assert.equal(disabled.status, 503);
});

test("malformed Basic authentication enablement fails configuration closed", () => {
  const result = authenticateAdminRequest(new Headers(), secureEnv({
    WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: "sometimes",
  }));

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
  assert.match(result.error, /must be either 'true' or 'false'/i);
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

test("duplicate admin session cookies are rejected instead of selecting an attacker-controlled value", () => {
  const env = secureEnv();
  const request = new Request("https://admin.example.com/api/auth/login", { method: "POST" });
  const cookie = createAdminSessionCookie("admin", request, env);
  assert.equal(cookie.ok, true);
  if (!cookie.ok) return;
  const pair = cookie.header.split(";")[0] ?? "";

  const result = authenticateAdminRequest(
    new Headers({ cookie: `${pair}; ${pair}` }),
    env
  );

  assert.equal(result.ok, false);
  assert.match(result.headers?.["set-cookie"] ?? "", /Max-Age=0/);
});

test("Basic credential rotation invalidates sessions signed by a separate secret", () => {
  const original = secureEnv();
  const cookie = createAdminSessionCookie(
    "admin",
    new Request("https://admin.example.com/api/auth/login", { method: "POST" }),
    original
  );
  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const authentication = authenticateAdminRequest(
    new Headers({ cookie: cookie.header.split(";")[0] ?? "" }),
    secureEnv({ WORKABLE_ADMIN_UI_PASSWORD: "a newly rotated administrator password" })
  );

  assert.equal(authentication.ok, false);
  assert.match(authentication.headers?.["set-cookie"] ?? "", /Max-Age=0/i);
});

test("current Entra email policy revokes an existing local session", () => {
  const original = entraEnv();
  const cookie = createAdminSessionCookie(
    "Entra Admin",
    new Request("https://admin.example.com/api/auth/entra/callback"),
    original,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const authentication = authenticateAdminRequest(
    new Headers({ cookie: cookie.header.split(";")[0] ?? "" }),
    entraEnv({ WORKABLE_ADMIN_ENTRA_ALLOWED_EMAILS: "different@example.com" })
  );

  assert.equal(authentication.ok, false);
  assert.equal(authentication.status, 403);
  assert.match(authentication.headers?.["set-cookie"] ?? "", /Max-Age=0/i);
  assert.ok(authentication.setCookieHeaders?.some((header) =>
    /workable_admin_entra_target_token/.test(header) && /Max-Age=0/i.test(header)
  ));
});

test("Entra sessions require a stable token subject", () => {
  const cookie = createAdminSessionCookie(
    "Entra Admin",
    new Request("https://admin.example.com/api/auth/entra/callback"),
    entraEnv(),
    "entra"
  );

  assert.equal(cookie.ok, false);
  if (cookie.ok) {
    return;
  }
  assert.equal(cookie.status, 503);
  assert.match(cookie.error, /stable subject/i);
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

  assert.match(authentication.sessionCookieHeader ?? "", /^__Host-workable_admin_session=/);
});

test("session renewal cannot extend beyond the configured absolute lifetime", () => {
  const env = secureEnv({
    WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS: "60",
    WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS: "90",
  });
  const originalNow = Date.now;
  const issuedAt = originalNow();
  Date.now = () => issuedAt;
  try {
    const cookie = createAdminSessionCookie(
      "admin",
      new Request("https://admin.example.com/api/auth/login", { method: "POST" }),
      env
    );
    assert.equal(cookie.ok, true);
    if (!cookie.ok) {
      return;
    }

    Date.now = () => issuedAt + 50_000;
    const renewed = authenticateAdminRequest(
      new Headers({ cookie: cookie.header.split(";")[0] ?? "" }),
      env,
      new Request("https://admin.example.com/workers")
    );
    assert.equal(renewed.ok, true);
    if (!renewed.ok) {
      return;
    }
    const renewedCookie = renewed.sessionCookieHeader?.split(";")[0] ?? "";
    assert.ok(renewedCookie);

    Date.now = () => issuedAt + 91_000;
    const expired = authenticateAdminRequest(
      new Headers({ cookie: renewedCookie }),
      env
    );
    assert.equal(expired.ok, false);
    assert.match(expired.headers?.["set-cookie"] ?? "", /Max-Age=0/i);
  } finally {
    Date.now = originalNow;
  }
});

test("logout generations invalidate delayed renewals without blocking a later sign-in", () => {
  const env = secureEnv({
    WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS: "60",
    WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS: "120",
  });
  const originalNow = Date.now;
  const issuedAt = originalNow();
  const request = new Request("https://admin.example.com/workers");
  Date.now = () => issuedAt;
  try {
    const initial = createAdminSessionCookie("admin", request, env);
    assert.equal(initial.ok, true);
    if (!initial.ok) return;

    Date.now = () => issuedAt + 50_000;
    const inFlight = authenticateAdminRequest(
      new Headers({ cookie: initial.header.split(";")[0] ?? "" }),
      env,
      request
    );
    assert.equal(inFlight.ok, true);
    if (!inFlight.ok) return;
    const delayedRenewal = inFlight.sessionCookieHeader?.split(";")[0] ?? "";
    assert.ok(delayedRenewal);

    Date.now = () => issuedAt + 51_000;
    const tombstone = createAdminLogoutTombstoneCookie(request, env)
      .split(";")[0] ?? "";
    const stale = authenticateAdminRequest(
      new Headers({ cookie: `${delayedRenewal}; ${tombstone}` }),
      env
    );
    assert.equal(stale.ok, false);
    assert.equal(stale.status, 401);

    Date.now = () => issuedAt + 52_000;
    const signedInAgain = createAdminSessionCookie(
      "admin",
      new Request(request.url, { headers: { cookie: tombstone } }),
      env
    );
    assert.equal(signedInAgain.ok, true);
    if (!signedInAgain.ok) return;
    assert.match(signedInAgain.logoutHeader ?? "", /__Host-workable_admin_logout=/);
    const current = authenticateAdminRequest(
      new Headers({
        cookie: `${signedInAgain.header.split(";")[0] ?? ""}; ${
          signedInAgain.logoutHeader?.split(";")[0] ?? ""
        }`,
      }),
      env
    );
    assert.equal(current.ok, true);
  } finally {
    Date.now = originalNow;
  }
});

test("logout generations reject a session issued by a process with a future clock", () => {
  const env = secureEnv();
  const originalNow = Date.now;
  const baseTime = originalNow();
  try {
    Date.now = () => baseTime + 4 * 60 * 1000;
    const futureSession = createAdminSessionCookie(
      "admin",
      new Request("https://admin.example.com/workers"),
      env
    );
    assert.equal(futureSession.ok, true);
    if (!futureSession.ok) return;

    Date.now = () => baseTime;
    const logout = createAdminLogoutTombstoneCookie(
      new Request("https://admin.example.com/workers"),
      env
    );
    const authentication = authenticateAdminRequest(
      new Headers({
        cookie: [futureSession.header, logout]
          .map((header) => header.split(";")[0] ?? "")
          .join("; "),
      }),
      env
    );

    assert.equal(authentication.ok, false);
    assert.equal(authentication.status, 401);
  } finally {
    Date.now = originalNow;
  }
});

test("session creation cannot replace the browser's current logout generation", () => {
  const env = secureEnv();
  const settings = getAdminSecuritySettings(env);
  const request = new Request("https://admin.example.com/workers", {
    headers: {
      cookie: createAdminLogoutTombstoneCookie(
        new Request("https://admin.example.com/workers"),
        env
      ).split(";")[0] ?? "",
    },
  });

  const session = createSignedAdminSessionCookie(
    {
      name: "admin",
      provider: "basic",
      logoutGeneration: "initial",
    },
    request,
    settings
  );

  assert.equal(session.ok, false);
  if (!session.ok) {
    assert.equal(session.status, 401);
    assert.match(session.error, /invalidated by logout/i);
  }
});

test("development logout barriers follow secure and HTTP cookie modes", () => {
  const env = secureEnv({ NODE_ENV: "development" });
  for (const url of ["https://admin.example.com/workers", "http://localhost:3000/workers"]) {
    const request = new Request(url);
    const session = createAdminSessionCookie("admin", request, env);
    assert.equal(session.ok, true);
    if (!session.ok) continue;

    const tombstone = createAdminLogoutTombstoneCookie(request, env);
    assert.match(
      tombstone,
      url.startsWith("https:")
        ? /^__Host-workable_admin_logout=/
        : /^workable_admin_logout=/
    );
    const authentication = authenticateAdminRequest(
      new Headers({
        cookie: [session.header, tombstone]
          .map((header) => header.split(";")[0] ?? "")
          .join("; "),
      }),
      env
    );
    assert.equal(authentication.ok, false);
  }
});

test("malformed or duplicate logout barriers fail an otherwise valid session closed", () => {
  const env = secureEnv();
  const request = new Request("https://admin.example.com/workers");
  const session = createAdminSessionCookie("admin", request, env);
  assert.equal(session.ok, true);
  if (!session.ok) return;
  const sessionPair = session.header.split(";")[0] ?? "";
  const logoutName = "__Host-workable_admin_logout";

  for (const cookie of [
    `${sessionPair}; ${logoutName}=malformed`,
    `${sessionPair}; ${logoutName}=one; ${logoutName}=two`,
    `${sessionPair}; ${logoutName}=${createSignedInvalidLogoutValue(env)}`,
  ]) {
    const authentication = authenticateAdminRequest(
      new Headers({ cookie }),
      env
    );
    assert.equal(authentication.ok, false);
    assert.equal(authentication.status, 401);
  }

  const newSession = createAdminSessionCookie(
    "admin",
    new Request(request.url, {
      headers: { cookie: `${logoutName}=malformed` },
    }),
    env
  );
  assert.equal(newSession.ok, false);
  if (!newSession.ok) {
    assert.equal(newSession.status, 401);
  }
});

test("logout without a usable signing secret emits only an expired safe session cookie", () => {
  const cookie = createAdminLogoutTombstoneCookie(
    new Request("https://admin.example.com/workers"),
    {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_DISABLED: "true",
      WORKABLE_ADMIN_UI_AUTH_PROVIDER: "basic",
      WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: "true",
    }
  );

  assert.match(cookie, /^__Host-workable_admin_session=;/);
  assert.match(cookie, /Max-Age=0/i);
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
    assert.equal(authentication.setCookieHeaders?.length, 34);
  } finally {
    Date.now = originalNow;
  }
});

test("expired Entra sessions also clear every delegated-token cookie chunk", () => {
  const env = entraEnv({
    WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS: "60",
  });
  const request = new Request("https://admin.example.com/api/auth/entra/callback");
  const cookie = createAdminSessionCookie(
    "Entra Admin",
    request,
    env,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(cookie.ok, true);
  if (!cookie.ok) {
    return;
  }

  const originalNow = Date.now;
  Date.now = () => originalNow() + 61_000;
  try {
    const authentication = authenticateAdminRequest(
      new Headers({ cookie: cookie.header.split(";")[0] ?? "" }),
      env
    );
    assert.equal(authentication.ok, false);
    if (authentication.ok) {
      return;
    }
    assert.match(authentication.headers?.["set-cookie"] ?? "", /Max-Age=0/i);
    assert.equal(authentication.setCookieHeaders?.length, 34);
    assert.ok(authentication.setCookieHeaders?.every((header) =>
      /workable_admin_entra_target_token/.test(header) && /Max-Age=0/i.test(header)
    ));
  } finally {
    Date.now = originalNow;
  }
});

test("legacy session cookies without an absolute lifetime are rejected", () => {
  const env = secureEnv();
  const settings = getAdminSecuritySettings(env);
  const now = Math.floor(Date.now() / 1000);
  const payload = Buffer.from(JSON.stringify({
    sub: "admin",
    provider: "basic",
    iat: now,
    exp: now + 3600,
  })).toString("base64url");
  const signature = signAdminValue(
    payload,
    env.WORKABLE_ADMIN_UI_SESSION_SECRET as string
  );

  const authentication = authenticateAdminRequest(
    new Headers({
      cookie: `${settings.sessionCookieName}=${payload}.${signature}`,
    }),
    env
  );

  assert.equal(authentication.ok, false);
  assert.equal(authentication.status, 401);
  assert.match(authentication.headers?.["set-cookie"] ?? "", /Max-Age=0/i);
});

test("admin UI auth provider selection remains independent from Basic enablement", () => {
  assert.equal(getAdminAuthProvider(secureEnv()), "basic");
  assert.equal(getAdminAuthProvider(entraEnv()), "entra");
  assert.equal(
    getAdminAuthProvider(entraEnv({ WORKABLE_ADMIN_UI_AUTH_PROVIDER: "entry" })),
    "entra"
  );
});

test("shared Basic authentication throttles header and form credential failures", () => {
  resetBasicAuthenticationAttemptsForTests();
  const env = secureEnv();
  const sourceHeaders = new Headers({ "x-forwarded-for": "192.0.2.10" });
  for (let attempt = 0; attempt < 4; attempt++) {
    const result = authenticateAdminRequest(
      new Headers({
        authorization: basic("admin", `wrong-${attempt}`),
        "x-forwarded-for": "192.0.2.10",
      }),
      env
    );
    assert.equal(result.ok, false);
    assertAuthenticationFailureStatus(result, 401);
  }

  const blocked = verifyAdminCredentials("admin", "still-wrong", env, sourceHeaders);
  assert.equal(blocked.ok, false);
  assert.equal(blocked.status, 429);
  assert.equal(blocked.headers?.["retry-after"], "60");

  const correctWhileBlocked = authenticateAdminRequest(
    new Headers({
      authorization: basic("admin", "correct horse battery staple"),
      "x-forwarded-for": "192.0.2.10",
    }),
    env
  );
  assertAuthenticationFailureStatus(correctWhileBlocked, 429);
  const distinctSource = verifyAdminCredentials(
    "admin",
    "correct horse battery staple",
    env,
    new Headers({ "x-forwarded-for": "192.0.2.11" })
  );
  assert.equal(distinctSource.ok, true);
  const originalNow = Date.now;
  Date.now = () => originalNow() + 61_000;
  try {
    const recovered = verifyAdminCredentials(
      "admin",
      "correct horse battery staple",
      env,
      sourceHeaders
    );
    assert.equal(recovered.ok, true);
  } finally {
    Date.now = originalNow;
    resetBasicAuthenticationAttemptsForTests();
  }
});

test("Basic authentication throttling cannot be bypassed by rotating or omitting source headers", () => {
  resetBasicAuthenticationAttemptsForTests();
  const env = secureEnv();
  for (let attempt = 0; attempt < 19; attempt++) {
    const result = verifyAdminCredentials(
      "admin",
      `wrong-${attempt}`,
      env,
      new Headers({ "cf-connecting-ip": `198.51.100.${attempt}` })
    );
    assertAuthenticationFailureStatus(result, 401);
  }

  const rotatedSourceBlocked = verifyAdminCredentials(
    "admin",
    "wrong-19",
    env,
    new Headers({ "cf-connecting-ip": "203.0.113.200" })
  );
  const correctFromAnotherSource = verifyAdminCredentials(
    "admin",
    "correct horse battery staple",
    env,
    new Headers({ "x-forwarded-for": "203.0.113.201" })
  );
  assertAuthenticationFailureStatus(rotatedSourceBlocked, 429);
  assertAuthenticationFailureStatus(correctFromAnotherSource, 429);

  resetBasicAuthenticationAttemptsForTests();
  for (let attempt = 0; attempt < 20; attempt++) {
    const result = verifyAdminCredentials("admin", `missing-source-${attempt}`, env);
    assertAuthenticationFailureStatus(result, attempt === 19 ? 429 : 401);
  }
  resetBasicAuthenticationAttemptsForTests();
});

test("Basic authentication blocks all credential guesses at the process-wide failure ceiling", () => {
  resetBasicAuthenticationAttemptsForTests();
  const env = secureEnv();
  for (let attempt = 0; attempt < 100; attempt++) {
    const result = verifyAdminCredentials(
      `candidate-${attempt}`,
      "wrong",
      env,
      new Headers({ "cf-connecting-ip": `192.0.2.${attempt}` })
    );
    assertAuthenticationFailureStatus(result, attempt === 99 ? 429 : 401);
  }

  const correctWhileGloballyBlocked = verifyAdminCredentials(
    "admin",
    "correct horse battery staple",
    env,
    new Headers({ "x-forwarded-for": "203.0.113.250" })
  );
  assertAuthenticationFailureStatus(correctWhileGloballyBlocked, 429);

  const invalidWhileGloballyBlocked = verifyAdminCredentials(
    "another-candidate",
    "wrong",
    env,
    new Headers({ "x-forwarded-for": "203.0.113.251" })
  );
  assertAuthenticationFailureStatus(invalidWhileGloballyBlocked, 429);
  resetBasicAuthenticationAttemptsForTests();
});

test("Basic authentication attempt tracking evicts old buckets at its fixed capacity", () => {
  resetBasicAuthenticationAttemptsForTests();
  const settings = getAdminSecuritySettings(secureEnv());
  const originalNow = Date.now;
  let now = originalNow();
  Date.now = () => now;
  try {
    for (let source = 0; source < 4_097; source++) {
      const result = authenticateBasicRequest(new Headers({
        authorization: basic(`candidate-${source}`, "wrong"),
        "x-forwarded-for": `198.51.100.${source}`,
      }), settings);
      assert.equal(result.ok, false);
      if (result.ok) {
        throw new Error("Expected the failed Basic attempt to be rejected.");
      }
      assert.equal(result.status, 401);
      if (source % 90 === 89) {
        now += 61_000;
      }
    }

    assert.ok(basicAuthenticationAttemptBucketCountForTests() <= 4_096);
  } finally {
    Date.now = originalNow;
    resetBasicAuthenticationAttemptsForTests();
  }
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
      WORKABLE_ADMIN_UI_SESSION_SECRET: "shared-session-secret-that-is-at-least-32-bytes",
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
      WORKABLE_ADMIN_UI_SESSION_SECRET: "shared-session-secret-that-is-at-least-32-bytes",
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
  assert.ok(setCookies.some((cookie) => cookie.startsWith("__Host-workable_admin_entra_state=")));
  assert.ok(setCookies.some((cookie) => cookie.startsWith("__Host-workable_admin_entra_nonce=")));
  assert.ok(setCookies.some((cookie) => cookie.startsWith("__Host-workable_admin_entra_verifier=")));
  assert.ok(setCookies.some((cookie) => cookie.startsWith("__Host-workable_admin_entra_next=")));
  assert.ok(setCookies.every((cookie) => !cookie.startsWith("__Host-") || /; Secure(?:;|$)/i.test(cookie)));
});

test("Entra login rejects malformed logout generation state", () => {
  const response = createEntraAuthorizationResponse(
    new Request("https://admin.example.com/api/auth/entra/login", {
      headers: { cookie: "__Host-workable_admin_logout=malformed" },
    }),
    entraEnv()
  );

  assert.equal(response.status, 401);
  assert.equal(response.headers.get("cache-control"), "no-store");
});

test("HTTP development Entra login uses unprefixed transaction cookies", () => {
  const response = createEntraAuthorizationResponse(
    new Request("http://localhost:3000/api/auth/entra/login"),
    entraEnv({ NODE_ENV: "development" })
  );
  const setCookies = getSetCookies(response.headers);

  assert.ok(setCookies.some((cookie) =>
    cookie.startsWith("workable_admin_entra_state=") && !/; Secure(?:;|$)/i.test(cookie)
  ));
  assert.equal(setCookies.some((cookie) => cookie.startsWith("__Host-")), false);
});

test("admin login return paths cannot escape the admin origin", () => {
  assert.equal(normalizeAdminReturnPath("/systems?name=Ops#workers"), "/systems?name=Ops#workers");
  assert.equal(normalizeAdminReturnPath("//evil.example.com"), "/");
  assert.equal(normalizeAdminReturnPath("/\\evil.example.com"), "/");
  assert.equal(normalizeAdminReturnPath("/\\\\evil.example.com"), "/");
  assert.equal(normalizeAdminReturnPath("https://evil.example.com/"), "/");
  assert.equal(normalizeAdminReturnPath("not-a-rooted-path"), "/");
  assert.equal(normalizeAdminReturnPath("/\\[::"), "/");
});

test("Entra login stores a safe fallback for a backslash return path", () => {
  const response = createEntraAuthorizationResponse(
    new Request("https://admin.example.com/api/auth/entra/login?next=/%5Cevil.example.com"),
    entraEnv()
  );
  const nextCookie = getSetCookies(response.headers).find((cookie) =>
    cookie.startsWith("__Host-workable_admin_entra_next=")
  );

  assert.ok(nextCookie);
  assert.match(nextCookie, /^__Host-workable_admin_entra_next=%2F;/);
});

test("unsolicited Entra callback failures preserve established session credentials", async () => {
  const response = await completeEntraLogin(
    new Request("https://admin.example.com/api/auth/entra/callback?error=access_denied"),
    entraEnv()
  );
  const cookies = getSetCookies(response.headers);

  assert.equal(response.status, 303);
  assert.ok(cookies.some((cookie) => cookie.startsWith("__Host-workable_admin_entra_state=")));
  assert.equal(cookies.some((cookie) => /workable_admin_session=/.test(cookie)), false);
  assert.equal(
    cookies.some((cookie) => /workable_admin_entra_target_token/.test(cookie)),
    false
  );
});

test("Entra callback cannot redirect through a tampered backslash return cookie", async () => {
  const { privateKey, publicKey } = generateKeyPairSync("rsa", {
    modulusLength: 2048,
  });
  const tokenHeader = Buffer.from(JSON.stringify({ alg: "RS256", kid: "test-key" }))
    .toString("base64url");
  const tokenPayload = Buffer.from(JSON.stringify({
    aud: "client-id",
    exp: Math.floor(Date.now() / 1000) + 300,
    iss: "https://login.microsoftonline.com/tenant-id/v2.0",
    name: "Admin",
    nonce: "expected-nonce",
    oid: "actor-id",
    tid: "tenant-id",
  })).toString("base64url");
  const signedContent = `${tokenHeader}.${tokenPayload}`;
  const idToken = `${signedContent}.${sign(
    "RSA-SHA256",
    Buffer.from(signedContent),
    privateKey
  ).toString("base64url")}`;
  const transactionStartedAt = Date.now() - 1_000;
  const logout = createAdminLogoutTombstoneCookie(
    new Request("https://admin.example.com/"),
    entraEnv()
  );
  const logoutPair = logout.split(";")[0] ?? "";
  const logoutGeneration = readLogoutGeneration(logoutPair);
  const request = new Request(
    "https://admin.example.com/api/auth/entra/callback?state=expected-state&code=authorization-code",
    {
      headers: {
        cookie: [
          `__Host-workable_admin_entra_state=${signedEntraStateValue(
            "expected-state",
            transactionStartedAt,
            logoutGeneration
          )}`,
          logoutPair,
          "__Host-workable_admin_entra_nonce=expected-nonce",
          "__Host-workable_admin_entra_verifier=expected-verifier",
          "__Host-workable_admin_entra_next=%2F%5Cevil.example.com%2Fadmin",
        ].join("; "),
      },
    }
  );

  const response = await completeEntraLogin(request, entraEnv(), async (url) => {
    const requestedUrl = String(url);
    if (requestedUrl.includes(".well-known/openid-configuration")) {
      return Response.json({
        issuer: "https://login.microsoftonline.com/tenant-id/v2.0",
        jwks_uri: "https://login.microsoftonline.com/keys",
        token_endpoint: "https://login.microsoftonline.com/token",
      });
    }

    if (requestedUrl === "https://login.microsoftonline.com/token") {
      return Response.json({ id_token: idToken });
    }

    return Response.json({
      keys: [{
        ...publicKey.export({ format: "jwk" }),
        alg: "RS256",
        kid: "test-key",
        use: "sig",
      }],
    });
  });

  assert.equal(response.status, 303);
  assert.equal(response.headers.get("location"), "https://admin.example.com/");
  const responseCookies = getSetCookies(response.headers);
  const sessionCookie = responseCookies.find((cookie) =>
    cookie.startsWith("__Host-workable_admin_session=")
  );
  assert.ok(sessionCookie);
  assert.ok(responseCookies.some((cookie) =>
    cookie.startsWith("__Host-workable_admin_logout=") && !/Max-Age=0/i.test(cookie)
  ));
});

test("logout invalidates a pre-logout Entra transaction before backchannel work", async () => {
  const env = entraEnv();
  const logout = createAdminLogoutTombstoneCookie(
    new Request("https://admin.example.com/"),
    env
  ).split(";")[0] ?? "";
  const transaction = entraCallbackRequest(signedEntraStateValue("forged-state"));
  const headers = new Headers(transaction.headers);
  headers.set("cookie", `${headers.get("cookie")}; ${logout}`);
  let fetchWasCalled = false;

  const response = await completeEntraLogin(
    new Request(transaction, { headers }),
    env,
    async () => {
      fetchWasCalled = true;
      return Response.json({});
    }
  );

  assert.equal(response.status, 303);
  assert.equal(fetchWasCalled, false);
  assert.match(
    new URL(response.headers.get("location") ?? "https://invalid/")
      .searchParams.get("error") ?? "",
    /invalidated by logout/i
  );
  assert.equal(
    getSetCookies(response.headers).some((cookie) =>
      cookie.startsWith("__Host-workable_admin_session=") && !/Max-Age=0/i.test(cookie)
    ),
    false
  );
});

test("forged Entra callback state cannot trigger backchannel requests", async () => {
  let fetchWasCalled = false;
  const fetcher: typeof fetch = async () => {
    fetchWasCalled = true;
    return Response.json({});
  };
  const unsignedResponse = await completeEntraLogin(
    entraCallbackRequest("forged-state"),
    entraEnv(),
    fetcher
  );
  const invalidSignatureResponse = await completeEntraLogin(
    entraCallbackRequest("forged-state.invalid-signature"),
    entraEnv(),
    fetcher
  );

  assert.equal(unsignedResponse.status, 303);
  assert.equal(invalidSignatureResponse.status, 303);
  assert.equal(fetchWasCalled, false);
  for (const response of [unsignedResponse, invalidSignatureResponse]) {
    const location = response.headers.get("location");
    assert.ok(location);
    assert.match(new URL(location).searchParams.get("error") ?? "", /state is not valid/i);
  }
});

test("expired, future, or malformed-generation Entra transactions cannot trigger backchannel requests", async () => {
  let fetchWasCalled = false;
  const now = Date.now();
  const signedStates = [
    signedEntraStateValue("forged-state", now - 10 * 60 * 1000),
    signedEntraStateValue("forged-state", now + 10 * 60 * 1000),
    signedEntraStateValue("forged-state", now, "not-a-generation"),
  ];
  for (const signedState of signedStates) {
    const response = await completeEntraLogin(
      entraCallbackRequest(signedState),
      entraEnv(),
      async () => {
        fetchWasCalled = true;
        return Response.json({});
      }
    );
    assert.equal(response.status, 303);
    assert.match(
      new URL(response.headers.get("location") ?? "https://invalid/")
        .searchParams.get("error") ?? "",
      /state is not valid/i
    );
  }
  assert.equal(fetchWasCalled, false);
});

test("production Entra callbacks ignore transplantable unprefixed transaction cookies", async () => {
  let fetchWasCalled = false;
  const state = "attacker-state";
  const response = await completeEntraLogin(
    new Request(
      `https://admin.example.com/api/auth/entra/callback?state=${state}&code=attacker-code`,
      {
        headers: {
          cookie: [
            `workable_admin_entra_state=${signedEntraStateValue(state)}`,
            "workable_admin_entra_nonce=attacker-nonce",
            "workable_admin_entra_verifier=attacker-verifier",
          ].join("; "),
        },
      }
    ),
    entraEnv(),
    async () => {
      fetchWasCalled = true;
      return Response.json({});
    }
  );

  assert.equal(response.status, 303);
  assert.equal(fetchWasCalled, false);
  assert.match(
    new URL(response.headers.get("location") ?? "https://invalid/")
      .searchParams.get("error") ?? "",
    /state is not valid/i
  );
});

test("Entra callbacks reject duplicate or malformed transaction cookies", async () => {
  let fetchWasCalled = false;
  const signedState = signedEntraStateValue("forged-state");
  const requests = [
    new Request(
      "https://admin.example.com/api/auth/entra/callback?state=forged-state&code=code",
      {
        headers: {
          cookie: [
            `__Host-workable_admin_entra_state=${signedState}`,
            `__Host-workable_admin_entra_state=${signedState}`,
            "__Host-workable_admin_entra_nonce=nonce",
            "__Host-workable_admin_entra_verifier=verifier",
          ].join("; "),
        },
      }
    ),
    new Request(
      "https://admin.example.com/api/auth/entra/callback?state=forged-state&code=code",
      {
        headers: {
          cookie: [
            "__Host-workable_admin_entra_state=%ZZ",
            "__Host-workable_admin_entra_nonce=nonce",
            "__Host-workable_admin_entra_verifier=verifier",
          ].join("; "),
        },
      }
    ),
  ];

  for (const request of requests) {
    const response = await completeEntraLogin(request, entraEnv(), async () => {
      fetchWasCalled = true;
      return Response.json({});
    });
    assert.equal(response.status, 303);
  }
  assert.equal(fetchWasCalled, false);
});

test("Entra callback refuses cross-origin discovery endpoints before sending credentials", async () => {
  const requestedUrls: string[] = [];
  const originalConsoleError = console.error;
  console.error = () => undefined;
  const response = await (async () => {
    try {
      return await completeEntraLogin(
        entraCallbackRequest(signedEntraStateValue("forged-state")),
        entraEnv({ WORKABLE_ADMIN_ENTRA_CLIENT_SECRET: "must-not-leave-authority" }),
        async (url) => {
          requestedUrls.push(String(url));
          return Response.json({
            issuer: "https://login.microsoftonline.com/tenant-id/v2.0",
            jwks_uri: "https://login.microsoftonline.com/keys",
            token_endpoint: "https://attacker.example.com/collect",
          });
        }
      );
    } finally {
      console.error = originalConsoleError;
    }
  })();

  assert.equal(response.status, 303);
  assert.equal(requestedUrls.length, 1);
  assert.ok(requestedUrls[0]?.includes(".well-known/openid-configuration"));
});

test("Entra metadata requests are coalesced and cached per fetch implementation", async () => {
  resetEntraBackchannelCachesForTests();
  let calls = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const fetcher: typeof fetch = async (_input, init) => {
    calls++;
    assert.ok(init?.signal);
    await gate;
    return Response.json({ token_endpoint: "https://login.example.com/token" });
  };
  const validate = (value: unknown): value is { token_endpoint: string } =>
    Boolean(value && typeof value === "object" &&
      typeof (value as { token_endpoint?: unknown }).token_endpoint === "string");

  const first = fetchCachedEntraJson(
    fetcher,
    "metadata:test",
    "https://login.example.com/.well-known/openid-configuration",
    validate
  );
  const second = fetchCachedEntraJson(
    fetcher,
    "metadata:test",
    "https://login.example.com/.well-known/openid-configuration",
    validate
  );
  release();

  assert.deepEqual(await first, { token_endpoint: "https://login.example.com/token" });
  assert.deepEqual(await second, { token_endpoint: "https://login.example.com/token" });
  await fetchCachedEntraJson(
    fetcher,
    "metadata:test",
    "https://login.example.com/.well-known/openid-configuration",
    validate
  );
  assert.equal(calls, 1);
});

test("Entra backchannel requests honor request cancellation", async () => {
  let outboundWasCancelled = false;
  const fetcher: typeof fetch = async (_input, init) =>
    await new Promise<Response>((_resolve, reject) => {
      assert.equal(init?.redirect, "error");
      init?.signal?.addEventListener("abort", () => {
        outboundWasCancelled = true;
        reject(new Error("cancelled"));
      }, { once: true });
    });
  const controller = new AbortController();
  const request = fetchEntraJson(
    fetcher,
    "https://login.example.com/token",
    { method: "POST" },
    controller.signal
  );

  controller.abort();
  await assert.rejects(request, /cancelled/);
  assert.equal(outboundWasCancelled, true);
});

test("Entra backchannel endpoints stay on the configured HTTPS authority", () => {
  assert.equal(
    validateEntraBackchannelUrl(
      "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
      "https://login.microsoftonline.com",
      "token endpoint"
    ),
    "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token"
  );
  for (const endpoint of [
    "http://login.microsoftonline.com/token",
    "https://metadata.internal/token",
    "https://user:password@login.microsoftonline.com/token",
    "https://login.microsoftonline.com/token#fragment",
    "not-a-url",
  ]) {
    assert.throws(
      () => validateEntraBackchannelUrl(
        endpoint,
        "https://login.microsoftonline.com",
        "token endpoint"
      ),
      /Microsoft Entra ID token endpoint/
    );
  }
});

test("Entra backchannel rejects declared and streamed JSON bodies over one MiB", async () => {
  const declaredOversized: typeof fetch = async () => new Response("{}", {
    headers: {
      "content-length": String(MAXIMUM_ENTRA_JSON_BYTES + 1),
      "content-type": "application/json",
    },
  });
  const streamedOversized: typeof fetch = async () => new Response(
    `"${"x".repeat(MAXIMUM_ENTRA_JSON_BYTES)}"`,
    { headers: { "content-type": "application/json" } }
  );
  const missingBody: typeof fetch = async () => new Response(null, { status: 204 });

  await assert.rejects(
    fetchEntraJson(declaredOversized, "https://login.microsoftonline.com/metadata"),
    /too large/
  );
  await assert.rejects(
    fetchEntraJson(streamedOversized, "https://login.microsoftonline.com/metadata"),
    /too large/
  );
  await assert.rejects(
    fetchEntraJson(missingBody, "https://login.microsoftonline.com/metadata"),
    /did not include JSON/
  );
});

test("Entra backchannel accepts a fragmented JSON body at the one MiB boundary", async () => {
  const encoder = new TextEncoder();
  const prefix = encoder.encode('{"value":"');
  const suffix = encoder.encode('"}');
  const valueLength = MAXIMUM_ENTRA_JSON_BYTES - prefix.byteLength - suffix.byteLength;
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(prefix);
      controller.enqueue(encoder.encode("x".repeat(valueLength)));
      controller.enqueue(suffix);
      controller.close();
    },
  });
  const fetcher: typeof fetch = async () => new Response(body);

  const result = await fetchEntraJson<{ value: string }>(
    fetcher,
    "https://login.microsoftonline.com/metadata"
  );

  assert.equal(result.value.value.length, valueLength);
});

test("Entra response limits remain fail-closed when body cancellation fails", async () => {
  let declaredCancellationAttempts = 0;
  const declaredBody = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(new TextEncoder().encode("{}"));
    },
    cancel() {
      declaredCancellationAttempts++;
      throw new Error("cancel failed");
    },
  });
  const declaredOversized: typeof fetch = async () => new Response(declaredBody, {
    headers: { "content-length": String(MAXIMUM_ENTRA_JSON_BYTES + 1) },
  });

  let streamedCancellationAttempts = 0;
  const streamedBody = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(new Uint8Array(MAXIMUM_ENTRA_JSON_BYTES + 1));
    },
    cancel() {
      streamedCancellationAttempts++;
      throw new Error("cancel failed");
    },
  });
  const streamedOversized: typeof fetch = async () => new Response(streamedBody);
  const bodylessDeclaredOversized: typeof fetch = async () => new Response(null, {
    headers: { "content-length": String(MAXIMUM_ENTRA_JSON_BYTES + 1) },
  });

  await assert.rejects(
    fetchEntraJson(declaredOversized, "https://login.microsoftonline.com/metadata"),
    /too large/
  );
  await assert.rejects(
    fetchEntraJson(streamedOversized, "https://login.microsoftonline.com/metadata"),
    /too large/
  );
  await assert.rejects(
    fetchEntraJson(bodylessDeclaredOversized, "https://login.microsoftonline.com/metadata"),
    /too large/
  );
  assert.equal(declaredCancellationAttempts, 1);
  assert.equal(streamedCancellationAttempts, 1);
});

test("Entra backchannel caches discard expired, failed, and malformed responses", async () => {
  resetEntraBackchannelCachesForTests();
  const originalNow = Date.now;
  let now = originalNow();
  Date.now = () => now;
  let calls = 0;
  const fetcher: typeof fetch = async (input) => {
    calls++;
    const url = String(input);
    if (url.endsWith("/failure")) {
      return new Response(JSON.stringify(calls === 1 ? {} : { valid: true }), {
        status: calls === 1 ? 503 : 200,
        headers: { "content-type": "application/json" },
      });
    }
    return Response.json(url.endsWith("/malformed") ? {} : { valid: true });
  };
  const validate = (value: unknown): value is { valid: true } =>
    (value as { valid?: unknown })?.valid === true;

  try {
    await assert.rejects(
      fetchCachedEntraJson(fetcher, "failure", "https://login.example.com/failure", validate),
      /failed \(503\)/
    );
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.deepEqual(
      await fetchCachedEntraJson(fetcher, "failure", "https://login.example.com/failure", validate),
      { valid: true }
    );
    await assert.rejects(
      fetchCachedEntraJson(fetcher, "malformed", "https://login.example.com/malformed", validate),
      /malformed/
    );

    const beforeExpiry = calls;
    await fetchCachedEntraJson(fetcher, "expires", "https://login.example.com/expires", validate);
    await fetchCachedEntraJson(fetcher, "expires", "https://login.example.com/expires", validate);
    assert.equal(calls, beforeExpiry + 1);
    now += 5 * 60_000 + 1;
    await fetchCachedEntraJson(fetcher, "expires", "https://login.example.com/expires", validate);
    assert.equal(calls, beforeExpiry + 2);

    for (let index = 0; index < 33; index++) {
      await fetchCachedEntraJson(
        fetcher,
        `bounded-${index}`,
        `https://login.example.com/bounded-${index}`,
        validate
      );
    }
  } finally {
    Date.now = originalNow;
    resetEntraBackchannelCachesForTests();
  }
});

test("cached Entra backchannel waits reject an already-cancelled caller", async () => {
  resetEntraBackchannelCachesForTests();
  const fetcher: typeof fetch = async () => Response.json({ valid: true });
  const validate = (value: unknown): value is { valid: true } =>
    (value as { valid?: unknown })?.valid === true;
  await fetchCachedEntraJson(
    fetcher,
    "metadata:cancelled",
    "https://login.example.com/metadata",
    validate
  );
  const controller = new AbortController();
  controller.abort();

  await assert.rejects(
    fetchCachedEntraJson(
      fetcher,
      "metadata:cancelled",
      "https://login.example.com/metadata",
      validate,
      controller.signal
    ),
    /cancelled/
  );
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
    "entra",
    TEST_ENTRA_SUBJECT
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

test("hosted Workable token endpoint returns the access token's actual remaining lifetime", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      {
        apiUrl: "https://workable.example.com/workable",
        scope: "api://actually-client-id/workable.access",
      },
    ]),
  });
  const originalNow = Date.now;
  const now = 1_800_000_000_000;
  Date.now = () => now;

  try {
    const cookieHeader = createEntraAuthenticatedCookieHeader(
      env,
      new Request("https://admin.example.com/"),
      {
        access_token: "hosted-api-access-token",
        expires_in: 900,
        refresh_token: "refresh-me",
        token_type: "Bearer",
      }
    );
    const response = await createEntraTargetAccessTokenResponse(
      new Request("https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable", {
        headers: { cookie: cookieHeader },
      }),
      env
    );

    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), {
      accessToken: "hosted-api-access-token",
      accessTokenExpiresInSeconds: 900,
    });
  } finally {
    Date.now = originalNow;
  }
});

test("Entra target tokens are bound to the immutable subject rather than display identity", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const request = new Request("https://admin.example.com/");
  const originalSession = createAdminSessionCookie(
    "Shared Admin",
    request,
    env,
    "entra",
    "subject-a"
  );
  assert.equal(originalSession.ok, true);
  if (!originalSession.ok) return;
  const targetCookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "admin-a-access-token",
      expires_in: 3600,
      refresh_token: "admin-a-refresh-token",
      token_type: "Bearer",
    },
    request,
    getAdminSecuritySettings(env),
    originalSession.identity
  );
  const otherSession = createAdminSessionCookie(
    "Shared Admin",
    request,
    env,
    "entra",
    "subject-b"
  );
  assert.equal(otherSession.ok, true);
  if (!otherSession.ok) {
    return;
  }

  const response = await createEntraTargetAccessTokenResponse(
    new Request(
      "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
      {
        headers: {
          cookie: [otherSession.header, ...targetCookies]
            .map((header) => header.split(";")[0] ?? "")
            .filter(Boolean)
            .join("; "),
        },
      }
    ),
    env,
    async () => {
      throw new Error("An identity-mismatched target token must not be used or refreshed.");
    }
  );

  assert.equal(response.status, 401);
  assert.ok(getSetCookies(response.headers).some((cookie) => /Max-Age=0/i.test(cookie)));
});

test("Entra target tokens cannot cross into a later session for the same subject", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const request = new Request("https://admin.example.com/");
  const firstSession = createAdminSessionCookie(
    "Same Admin",
    request,
    env,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  const laterSession = createAdminSessionCookie(
    "Same Admin",
    request,
    env,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(firstSession.ok, true);
  assert.equal(laterSession.ok, true);
  if (!firstSession.ok || !laterSession.ok) return;

  const targetCookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "first-session-access-token",
      expires_in: 3600,
      refresh_token: "first-session-refresh-token",
      token_type: "Bearer",
    },
    request,
    getAdminSecuritySettings(env),
    firstSession.identity
  );
  const response = await createEntraTargetAccessTokenResponse(
    new Request(
      "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
      {
        headers: {
          cookie: [laterSession.header, ...targetCookies]
            .map((header) => header.split(";")[0] ?? "")
            .filter(Boolean)
            .join("; "),
        },
      }
    ),
    env,
    async () => {
      throw new Error("A delegated token from another session must not be used.");
    }
  );

  assert.equal(response.status, 401);
  assert.ok(getSetCookies(response.headers).some((cookie) => /Max-Age=0/i.test(cookie)));
});

test("duplicate delegated-token cookie chunks are rejected", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://client-id/workable.access",
    }]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    { access_token: "target-token", expires_in: 3600, refresh_token: "refresh-token" }
  );
  const parts = cookieHeader.split("; ").find((cookie) =>
    /workable_admin_entra_target_token\.[^.]+\.parts=/.test(cookie));
  assert.ok(parts);

  const response = await createEntraTargetAccessTokenResponse(
    new Request(
      "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
      { headers: { cookie: `${cookieHeader}; ${parts}` } }
    ),
    env,
    async () => { throw new Error("Duplicate state must be rejected before refresh."); }
  );

  assert.equal(response.status, 401);
  assert.ok(getSetCookies(response.headers).some((cookie) => /Max-Age=0/.test(cookie)));
});

test("delegated-token cleanup bounds attacker-supplied snapshot names", () => {
  const cookie = Array.from({ length: 10 }, (_, index) => {
    const id = `00000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`;
    return `__Host-workable_admin_entra_target_token.${id}.parts=1`;
  }).concat("__Host-workable_admin_entra_target_token.attacker.parts=16").join("; ");

  const expired = createExpiredEntraTargetTokenCookies(new Headers({ cookie }));

  assert.equal(expired.length, 42);
  assert.equal(expired.some((header) => header.includes("attacker")), false);
});

test("production snapshot discovery ignores sibling-domain development cookie names", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const legitimate = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      access_token: "legitimate-access-token",
      expires_in: 3600,
      refresh_token: "legitimate-refresh-token",
    }
  );
  const siblingCookies = Array.from({ length: 4 }, (_, index) => {
    const id = `00000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`;
    return `workable_admin_entra_target_token.${id}.parts=1; workable_admin_entra_target_token.${id}.0=attacker`;
  }).join("; ");

  const response = await createEntraTargetAccessTokenResponse(
    new Request(
      "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
      { headers: { cookie: `${siblingCookies}; ${legitimate}` } }
    ),
    env,
    async () => {
      throw new Error("A valid unexpired production snapshot must not be crowded out.");
    }
  );

  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), {
    accessToken: "legitimate-access-token",
    accessTokenExpiresInSeconds: 3600,
  });
});

test("Entra target tokens are rejected after the target configuration changes", async () => {
  const originalEnv = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/original.access",
    }]),
  });
  const changedEnv = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/replacement.access",
    }]),
  });
  const request = new Request("https://admin.example.com/");
  const originalSession = createAdminSessionCookie(
    "admin",
    request,
    originalEnv,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(originalSession.ok, true);
  if (!originalSession.ok) return;
  const targetCookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "old-scope-access-token",
      expires_in: 3600,
      refresh_token: "old-scope-refresh-token",
      token_type: "Bearer",
    },
    request,
    getAdminSecuritySettings(originalEnv),
    originalSession.identity
  );

  const response = await createEntraTargetAccessTokenResponse(
    new Request(
      "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
      {
        headers: {
          cookie: [originalSession.header, ...targetCookies]
            .map((header) => header.split(";")[0] ?? "")
            .filter(Boolean)
            .join("; "),
        },
      }
    ),
    changedEnv,
    async () => {
      throw new Error("A token bound to stale target configuration must not be used.");
    }
  );

  assert.equal(response.status, 401);
  assert.ok(getSetCookies(response.headers).some((cookie) => /Max-Age=0/i.test(cookie)));
});

test("switching to Basic authentication clears Entra target tokens without forwarding them", async () => {
  const sharedSecret = "shared-provider-switch-secret-that-is-at-least-32-bytes";
  const targetConfiguration = JSON.stringify([{
    apiUrl: "https://workable.example.com/workable",
    scope: "api://actually-client-id/workable.access",
  }]);
  const entraSettings = getAdminSecuritySettings(entraEnv({
    WORKABLE_ADMIN_UI_SESSION_SECRET: sharedSecret,
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: targetConfiguration,
  }));
  const request = new Request("https://admin.example.com/");
  const entraSession = createAdminSessionCookie(
    "entra-admin",
    request,
    entraEnv({
      WORKABLE_ADMIN_UI_SESSION_SECRET: sharedSecret,
      WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: targetConfiguration,
    }),
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(entraSession.ok, true);
  if (!entraSession.ok) return;
  const tokenCookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "must-not-be-forwarded",
      expires_in: 3600,
      refresh_token: "must-not-be-refreshed",
      token_type: "Bearer",
    },
    request,
    entraSettings,
    entraSession.identity
  );
  let forwardedAuthorization: string | null = "not-called";

  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
        cookie: tokenCookies
          .map((header) => header.split(";")[0] ?? "")
          .filter(Boolean)
          .join("; "),
      },
    }),
    ["host"],
    {
      env: secureEnv({
        WORKABLE_ADMIN_UI_SESSION_SECRET: sharedSecret,
        WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: targetConfiguration,
      }),
      fetch: async (_url, init) => {
        forwardedAuthorization = new Headers(init?.headers).get("authorization");
        return Response.json({ ok: true });
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(forwardedAuthorization, null);
  assert.ok(getSetCookies(response.headers).some((cookie) => /Max-Age=0/i.test(cookie)));
});

test("hosted Workable token endpoint can force one refresh for a rejected realtime token", async () => {
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
      access_token: "still-current-but-rejected-token",
      expires_in: 3600,
      refresh_token: "refresh-me",
      token_type: "Bearer",
    }
  );
  const requestedUrls: string[] = [];
  const response = await createEntraTargetAccessTokenResponse(
    new Request("https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable", {
      headers: {
        cookie: cookieHeader,
        "x-workable-force-token-refresh": "true",
      },
    }),
    env,
    async (url) => {
      requestedUrls.push(String(url));
      if (String(url).includes(".well-known/openid-configuration")) {
        return new Response(JSON.stringify({
          token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
        }), {
          status: 200,
          headers: { "content-type": "application/json" },
        });
      }

      return new Response(JSON.stringify({
        access_token: "forced-replacement-token",
        expires_in: 3600,
        refresh_token: "next-refresh-token",
        token_type: "Bearer",
      }), {
        status: 200,
        headers: { "content-type": "application/json" },
      });
    }
  );

  assert.equal(response.status, 200);
  const body = await response.json() as {
    accessToken: string;
    accessTokenExpiresInSeconds: number;
  };
  assert.equal(body.accessToken, "forced-replacement-token");
  assert.ok(body.accessTokenExpiresInSeconds >= 3599);
  assert.equal(requestedUrls.length, 2);
  assert.ok(requestedUrls[0]?.includes(".well-known/openid-configuration"));
  assert.ok(requestedUrls[1]?.endsWith("/oauth2/v2.0/token"));
});

test("refreshed Entra target cookies cannot outlive the absolute admin session", async () => {
  resetEntraBackchannelCachesForTests();
  const env = entraEnv({
    WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS: "120",
    WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS: "150",
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const originalNow = Date.now;
  const issuedAt = 1_800_000_000_000;
  Date.now = () => issuedAt;
  try {
    const cookieHeader = createEntraAuthenticatedCookieHeader(
      env,
      new Request("https://admin.example.com/"),
      {
        access_token: "rejected-token",
        expires_in: 3600,
        refresh_token: "refresh-near-absolute-expiry",
        token_type: "Bearer",
      }
    );
    Date.now = () => issuedAt + 100_000;
    const response = await createEntraTargetAccessTokenResponse(
      new Request(
        "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
        {
          headers: {
            cookie: cookieHeader,
            "x-workable-force-token-refresh": "true",
          },
        }
      ),
      env,
      async (url) => Response.json(
        String(url).includes(".well-known/openid-configuration")
          ? { token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token" }
          : {
              access_token: "replacement-token",
              expires_in: 3600,
              refresh_token: "rotated-refresh-token",
              token_type: "Bearer",
            }
      )
    );

    assert.equal(response.status, 200);
    const targetCookies = getSetCookies(response.headers).filter((header) =>
      /workable_admin_entra_target_token/.test(header) && !/Max-Age=0/i.test(header)
    );
    assert.ok(targetCookies.length > 0);
    assert.ok(targetCookies.every((header) => /Max-Age=50(?:;|$)/i.test(header)));
  } finally {
    Date.now = originalNow;
  }
});

test("concurrent forced Entra refreshes share one rotation-safe token exchange", async () => {
  resetEntraBackchannelCachesForTests();
  resetEntraRefreshCoordinatorsForTests();
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      access_token: "rejected-token",
      // A rejected token's later advertised expiry must not outrank the
      // coordinator's successfully refreshed replacement.
      expires_in: 7200,
      refresh_token: "refresh-me-once",
      token_type: "Bearer",
    }
  );
  let tokenCalls = 0;
  const fetcher: typeof fetch = async (url) => {
    if (String(url).includes(".well-known/openid-configuration")) {
      return Response.json({
        token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
      });
    }
    tokenCalls++;
    await new Promise((resolve) => setTimeout(resolve, 5));
    return Response.json({
      access_token: "shared-replacement-token",
      expires_in: 600,
      refresh_token: "rotated-refresh-token",
      token_type: "Bearer",
    });
  };
  const createRequest = (signal?: AbortSignal) => new Request(
    "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
    {
      signal,
      headers: {
        cookie: cookieHeader,
        "x-workable-force-token-refresh": "true",
      },
    }
  );
  const disconnectedWaiter = new AbortController();

  const responses = Promise.all([
    createEntraTargetAccessTokenResponse(createRequest(disconnectedWaiter.signal), env, fetcher),
    createEntraTargetAccessTokenResponse(createRequest(), env, fetcher),
  ]);
  disconnectedWaiter.abort();
  const [first, second] = await responses;

  assert.equal(tokenCalls, 1);
  assert.equal((await first.json() as { accessToken: string }).accessToken, "shared-replacement-token");
  assert.equal((await second.json() as { accessToken: string }).accessToken, "shared-replacement-token");
});

test("concurrent refresh cleanup remains bounded to each request's snapshot", async () => {
  resetEntraBackchannelCachesForTests();
  resetEntraRefreshCoordinatorsForTests();
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const originalCookies = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      access_token: "rejected-token",
      expires_in: 7200,
      refresh_token: "shared-refresh-token",
    }
  );
  const rootMatch = originalCookies.match(
    /(__Host-workable_admin_entra_target_token\.[0-9a-f-]{36})\.parts=/i
  );
  assert.ok(rootMatch?.[1]);
  if (!rootMatch?.[1]) return;

  const aliases = Array.from({ length: 32 }, (_, index) =>
    `__Host-workable_admin_entra_target_token.00000000-0000-4000-8000-${index
      .toString(16)
      .padStart(12, "0")}`
  );
  let tokenCalls = 0;
  const fetcher: typeof fetch = async (url) => {
    if (String(url).includes(".well-known/openid-configuration")) {
      return Response.json({
        token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
      });
    }
    tokenCalls++;
    await new Promise((resolve) => setTimeout(resolve, 5));
    return Response.json({
      access_token: "shared-replacement-token",
      expires_in: 600,
      refresh_token: "rotated-refresh-token",
      token_type: "Bearer",
    });
  };

  const responses = await Promise.all(aliases.map((alias) =>
    createEntraTargetAccessTokenResponse(
      new Request(
        "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
        {
          headers: {
            cookie: originalCookies.replaceAll(rootMatch[1]!, alias),
            "x-workable-force-token-refresh": "true",
          },
        }
      ),
      env,
      fetcher
    )
  ));

  assert.equal(tokenCalls, 1);
  for (const [index, response] of responses.entries()) {
    assert.equal(response.status, 200);
    const setCookies = getSetCookies(response.headers);
    assert.ok(setCookies.length <= 20);
    const cleanup = setCookies.filter((cookie) => /Max-Age=0/i.test(cookie));
    assert.ok(cleanup.length > 0);
    assert.ok(cleanup.every((cookie) => cookie.startsWith(`${aliases[index]}.`)));
  }
});

test("concurrent failed Entra refreshes share one failure without retry amplification", async () => {
  resetEntraBackchannelCachesForTests();
  resetEntraRefreshCoordinatorsForTests();
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    { refresh_token: "failing-refresh-token" }
  );
  let tokenCalls = 0;
  const fetcher: typeof fetch = async (url) => {
    if (String(url).includes(".well-known/openid-configuration")) {
      return Response.json({
        token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
      });
    }
    tokenCalls++;
    await new Promise((resolve) => setTimeout(resolve, 5));
    return Response.json({ error: "invalid_grant" }, { status: 400 });
  };
  const createRequest = () => new Request(
    "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable",
    { headers: { cookie: cookieHeader } }
  );

  const responses = await Promise.all([
    createEntraTargetAccessTokenResponse(createRequest(), env, fetcher),
    createEntraTargetAccessTokenResponse(createRequest(), env, fetcher),
  ]);

  assert.equal(tokenCalls, 1);
  assert.deepEqual(responses.map((response) => response.status), [401, 401]);
});

test("concurrent target refreshes serialize rotation and preserve both target bindings", async () => {
  resetEntraBackchannelCachesForTests();
  resetEntraRefreshCoordinatorsForTests();
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      { apiUrl: "https://first.example.com/workable", scope: "api://first/access" },
      { apiUrl: "https://second.example.com/workable", scope: "api://second/access" },
    ]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    { refresh_token: "initial-refresh-token" }
  );
  const refreshTokens: string[] = [];
  let activeTokenCalls = 0;
  let maximumActiveTokenCalls = 0;
  const fetcher: typeof fetch = async (url, init) => {
    if (String(url).includes(".well-known/openid-configuration")) {
      return Response.json({
        token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
      });
    }
    activeTokenCalls++;
    maximumActiveTokenCalls = Math.max(maximumActiveTokenCalls, activeTokenCalls);
    const body = init?.body as URLSearchParams;
    refreshTokens.push(body.get("refresh_token") ?? "");
    const call = refreshTokens.length;
    await new Promise((resolve) => setTimeout(resolve, 5));
    activeTokenCalls--;
    return Response.json({
      access_token: `target-token-${call}`,
      expires_in: 3600,
      refresh_token: `rotated-token-${call}`,
      token_type: "Bearer",
    });
  };
  const requestFor = (apiUrl: string) => new Request(
    `https://admin.example.com/api/auth/entra/workable-token?apiUrl=${encodeURIComponent(apiUrl)}`,
    { headers: { cookie: cookieHeader } }
  );

  const [first, second] = await Promise.all([
    createEntraTargetAccessTokenResponse(
      requestFor("https://first.example.com/workable"), env, fetcher),
    createEntraTargetAccessTokenResponse(
      requestFor("https://second.example.com/workable"), env, fetcher),
  ]);

  assert.equal(first.status, 200);
  assert.equal(second.status, 200);
  assert.equal(maximumActiveTokenCalls, 1);
  assert.deepEqual(refreshTokens, ["initial-refresh-token", "rotated-token-1"]);
  const mergedCookie = getSetCookies(second.headers)
    .map((cookie) => cookie.split(";")[0])
    .filter(Boolean)
    .concat(cookieHeader.split("; ").filter((cookie) =>
      cookie.startsWith(`${getAdminSecuritySettings(env).sessionCookieName}=`)
    ))
    .join("; ");
  const cachedFirst = await createEntraTargetAccessTokenResponse(
    new Request(
      "https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Ffirst.example.com%2Fworkable",
      { headers: { cookie: mergedCookie } }
    ),
    env,
    async () => {
      throw new Error("Merged target binding should not refresh.");
    }
  );
  assert.equal((await cachedFirst.json() as { accessToken: string }).accessToken, "target-token-1");
});

test("out-of-order delegated-token responses merge immutable snapshots without refresh rollback", async () => {
  resetEntraBackchannelCachesForTests();
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([
      { apiUrl: "https://first.example.com/workable", scope: "api://first/access" },
      { apiUrl: "https://second.example.com/workable", scope: "api://second/access" },
    ]),
  });
  const initial = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    { refresh_token: "shared-refresh-token" }
  );
  const requestFor = (apiUrl: string, cookie = initial) => new Request(
    `https://admin.example.com/api/auth/entra/workable-token?apiUrl=${encodeURIComponent(apiUrl)}`,
    { headers: { cookie } }
  );
  const fetcher: typeof fetch = async (url, init) => Response.json(
    String(url).includes(".well-known/openid-configuration")
      ? { token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token" }
      : {
          access_token: `token-for-${(init?.body as URLSearchParams).get("scope")}`,
          expires_in: 3600,
          refresh_token: `rotated-${(init?.body as URLSearchParams).get("scope")}`,
          token_type: "Bearer",
        }
  );

  resetEntraRefreshCoordinatorsForTests();
  const first = await createEntraTargetAccessTokenResponse(
    requestFor("https://first.example.com/workable"), env, fetcher);
  resetEntraRefreshCoordinatorsForTests();
  const second = await createEntraTargetAccessTokenResponse(
    requestFor("https://second.example.com/workable"), env, fetcher);
  const sessionPair = initial.split("; ").find((value) =>
    value.startsWith(`${getAdminSecuritySettings(env).sessionCookieName}=`))!;
  const snapshotPairs = [second, first].flatMap((response) =>
    getSetCookies(response.headers)
      .filter((cookie) => !/Max-Age=0/i.test(cookie))
      .map((cookie) => cookie.split(";")[0]!)
      .filter((cookie) => /workable_admin_entra_target_token/.test(cookie)));
  const combined = [sessionPair, ...snapshotPairs].join("; ");

  for (const apiUrl of [
    "https://first.example.com/workable",
    "https://second.example.com/workable",
  ]) {
    const cached = await createEntraTargetAccessTokenResponse(
      requestFor(apiUrl, combined),
      env,
      async () => { throw new Error("Merged immutable snapshots must not refresh."); }
    );
    assert.equal(cached.status, 200);
    assert.match((await cached.json() as { accessToken: string }).accessToken, /^token-for-/);
    assert.ok(getSetCookies(cached.headers).some((cookie) => /Max-Age=0/.test(cookie)));
  }
});

test("hosted token refresh refuses cross-origin discovery endpoints before sending secrets", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_CLIENT_SECRET: "must-not-leave-authority",
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const cookieHeader = createEntraAuthenticatedCookieHeader(
    env,
    new Request("https://admin.example.com/"),
    {
      access_token: "rejected-token",
      expires_in: 3600,
      refresh_token: "must-not-leave-authority",
      token_type: "Bearer",
    }
  );
  const requestedUrls: string[] = [];

  const response = await createEntraTargetAccessTokenResponse(
    new Request("https://admin.example.com/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Fworkable.example.com%2Fworkable", {
      headers: {
        cookie: cookieHeader,
        "x-workable-force-token-refresh": "true",
      },
    }),
    env,
    async (url) => {
      requestedUrls.push(String(url));
      return Response.json({
        token_endpoint: "https://attacker.example.com/collect",
      });
    }
  );

  assert.equal(response.status, 401);
  assert.equal(requestedUrls.length, 1);
  assert.ok(requestedUrls[0]?.includes(".well-known/openid-configuration"));
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

test("proxy refuses hosted API redirects instead of escaping the target allowlist", async () => {
  let redirectMode: RequestRedirect | undefined;
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
    }),
    ["host"],
    {
      env: secureEnv(),
      fetch: async (_url, init) => {
        redirectMode = init?.redirect;
        throw new TypeError("redirect mode is error");
      },
    }
  );

  assert.equal(redirectMode, "error");
  assert.equal(response.status, 502);
  assert.deepEqual(await response.json(), {
    error: "Unable to reach the Workable HTTP API.",
  });
});

test("proxy preserves an oversized response when request cancellation fails", async () => {
  let cancelled = false;
  let fetched = false;
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(new TextEncoder().encode("12345"));
    },
    cancel() {
      cancelled = true;
      throw new Error("request cancellation failed");
    },
  });
  const request = new Request("https://admin.example.com/api/workable/work/example", {
    method: "POST",
    headers: {
      authorization: basic("admin", "correct horse battery staple"),
      origin: "https://admin.example.com",
    },
    body,
    duplex: "half",
  } as RequestInit & { duplex: "half" });

  const response = await proxyWorkableRequest(request, ["work", "example"], {
    env: secureEnv({ WORKABLE_ADMIN_UI_MAX_BODY_BYTES: "4" }),
    fetch: async () => {
      fetched = true;
      return new Response();
    },
  });

  assert.equal(response.status, 413);
  assert.equal(cancelled, true);
  assert.equal(fetched, false);
  assert.equal(request.body?.locked, false);
  assert.deepEqual(await response.json(), {
    error: "Workable admin UI proxy request body is too large.",
  });
});

test("proxy returns a stable client error for interrupted request bodies", async () => {
  let fetched = false;
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.error(new Error("request stream secret"));
    },
  });
  const request = new Request("https://admin.example.com/api/workable/work/example", {
    method: "POST",
    headers: {
      authorization: basic("admin", "correct horse battery staple"),
      origin: "https://admin.example.com",
    },
    body,
    duplex: "half",
  } as RequestInit & { duplex: "half" });

  const response = await proxyWorkableRequest(request, ["work", "example"], {
    env: secureEnv(),
    fetch: async () => {
      fetched = true;
      return new Response();
    },
  });

  assert.equal(response.status, 400);
  assert.equal(fetched, false);
  assert.equal(request.body?.locked, false);
  assert.deepEqual(await response.json(), {
    error: "Workable admin UI proxy request body could not be read.",
  });
});

test("proxy surfaces hosted issuer mismatch hints from bearer challenges", async () => {
  let cancelled = false;
  const upstreamBody = new ReadableStream<Uint8Array>({
    cancel() {
      cancelled = true;
    },
  });
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
        new Response(upstreamBody, {
          status: 401,
          headers: {
            "content-type": "application/json",
            "www-authenticate": 'Bearer error="invalid_token", error_description="IDX10205: Issuer validation failed."',
          },
        }),
    }
  );

  assert.equal(response.status, 401);
  assert.equal(cancelled, true);
  assert.deepEqual(await response.json(), {
    error:
      "The hosted Workable API rejected the bearer token because the token issuer does not match its Entra configuration. Check that the target API app registration is configured to issue v2 access tokens.",
  });
});

test("proxy surfaces hosted audience mismatch hints from bearer challenges", async () => {
  const upstreamBody = new ReadableStream<Uint8Array>({
    cancel() {
      throw new Error("upstream cancellation failed");
    },
  });
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
        new Response(upstreamBody, {
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

test("proxy rewrites a bodyless hosted bearer challenge", async () => {
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
        new Response(null, {
          status: 401,
          headers: {
            "www-authenticate": 'Bearer error="invalid_token"',
          },
        }),
    }
  );

  assert.equal(response.status, 401);
  assert.deepEqual(await response.json(), {
    error:
      "The hosted Workable API rejected the bearer token. Check the target API token version, audience, and delegated scope configuration.",
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

test("proxy streams hosted responses and forwards client cancellation", async () => {
  const cancellation = new AbortController();
  const upstream = new Response('{"streamed":true}', {
    status: 200,
    headers: { "content-type": "application/json" },
  });
  Object.defineProperty(upstream, "arrayBuffer", {
    value: async () => {
      throw new Error("The proxy must not buffer the hosted response.");
    },
  });
  let forwardedSignal: AbortSignal | null | undefined;
  const request = new Request(
    "https://admin.example.com/api/workable/execution-diagnostics",
    {
      headers: {
        authorization: basic("admin", "correct horse battery staple"),
      },
      signal: cancellation.signal,
    }
  );

  const response = await proxyWorkableRequest(
    request,
    ["execution-diagnostics"],
    {
      env: secureEnv(),
      fetch: async (_url, init) => {
        forwardedSignal = init?.signal;
        return upstream;
      },
    }
  );

  assert.equal(response.status, 200);
  assert.equal(forwardedSignal, request.signal);
  cancellation.abort();
  assert.equal(forwardedSignal?.aborted, true);
  assert.equal(await response.text(), '{"streamed":true}');
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

test("production rejects delegated Entra target APIs that would receive a token over HTTP", async () => {
  const env = entraEnv({
    WORKABLE_API_URL: "http://workable.example.com/workable",
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "http://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  let fetchWasCalled = false;
  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host"),
    ["host"],
    {
      env,
      fetch: async () => {
        fetchWasCalled = true;
        return Response.json({});
      },
    }
  );

  assert.equal(response.status, 503);
  assert.equal(fetchWasCalled, false);
  assert.match((await response.json() as { error: string }).error, /must use https in production/i);

  const development = authenticateAdminRequest(new Headers(), {
    ...env,
    NODE_ENV: "development",
  });
  assert.equal(development.ok, false);
  if (!development.ok) {
    assert.equal(development.status, 401);
  }
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
      /workable_admin_entra_target_token\.[^. ;]+\.parts=/.test(cookie) && !/Max-Age=0/i.test(cookie)
    )
  );
});

test("proxy preserves rotated delegated-token cookies when the hosted request fails", async () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
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

  const response = await proxyWorkableRequest(
    new Request("https://admin.example.com/api/workable/host", {
      headers: { cookie: cookieHeader },
    }),
    ["host"],
    {
      env,
      fetch: async (url) => {
        if (String(url).includes(".well-known/openid-configuration")) {
          return new Response(JSON.stringify({
            token_endpoint: "https://login.microsoftonline.com/tenant-id/oauth2/v2.0/token",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }
        if (String(url).endsWith("/oauth2/v2.0/token")) {
          return new Response(JSON.stringify({
            access_token: "rotated-access-token",
            expires_in: 3600,
            refresh_token: "rotated-refresh-token",
            token_type: "Bearer",
          }), {
            status: 200,
            headers: { "content-type": "application/json" },
          });
        }
        throw new Error("Hosted API is unreachable after token rotation.");
      },
    }
  );

  assert.equal(response.status, 502);
  assert.ok(getSetCookies(response.headers).some((cookie) =>
    /workable_admin_entra_target_token\.[^. ;]+\.parts=/.test(cookie) &&
      !/Max-Age=0/i.test(cookie)
  ));
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
  const request = new Request("https://admin.example.com/");
  const session = createAdminSessionCookie(
    "admin",
    request,
    env,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(session.ok, true);
  if (!session.ok) return;

  const cookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "a".repeat(80_000),
      expires_in: 3600,
      refresh_token: "b".repeat(80_000),
      token_type: "Bearer",
    },
    request,
    getAdminSecuritySettings(env),
    session.identity
  );

  assert.deepEqual(cookies, createExpiredEntraTargetTokenCookies());
});

test("Entra target token cookies cannot be created for a non-Entra identity", () => {
  const env = entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{
      apiUrl: "https://workable.example.com/workable",
      scope: "api://actually-client-id/workable.access",
    }]),
  });
  const cookies = createEntraTargetTokenCookieHeaders(
    {
      access_token: "must-not-be-stored",
      expires_in: 3600,
      refresh_token: "must-not-be-stored",
    },
    new Request("https://admin.example.com/"),
    getAdminSecuritySettings(env),
    { name: "basic-admin", provider: "basic" }
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
        enabled: true,
        username: "admin",
        password: "correct horse battery staple",
      },
      sessionSecret: "replace-with-a-different-long-random-secret",
      sessionMaxAgeSeconds: 120,
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

test("string false cannot enable anonymous access through the config file", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-"));
  const configPath = join(temp, "workable-admin.config.local.json");
  writeFileSync(configPath, JSON.stringify({ allowAnonymous: "false" }));

  try {
    const env = {
      NODE_ENV: "development",
      WORKABLE_ADMIN_CONFIG_PATH: configPath,
    };
    const settings = getAdminSecuritySettings(env);
    const authentication = authenticateAdminRequest(new Headers(), env);

    assert.equal(settings.allowAnonymous, false);
    assert.match(settings.configError ?? "", /allowAnonymous must be a boolean/i);
    assert.equal(authentication.ok, false);
    assert.equal(authentication.status, 503);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test("wrong-typed config file security values fail closed at the file boundary", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-"));
  const configPath = join(temp, "workable-admin.config.local.json");
  const cases: ReadonlyArray<readonly [string, unknown, RegExp]> = [
    ["root", [], /must contain a JSON object/i],
    ["API URL", { apiUrl: 42 }, /apiUrl must be a string/i],
    ["allowed API URLs", { allowedApiUrls: ["https://workable.example.com", 42] }, /allowedApiUrls must be an array of strings/i],
    ["Basic container", { basicAuth: [] }, /basicAuth must be a JSON object/i],
    ["Basic username", { basicAuth: { username: 42 } }, /basicAuth\.username must be a string/i],
    ["Entra container", { entraId: "tenant" }, /entraId must be a JSON object/i],
    ["Entra tenant", { entraId: { tenantId: 42 } }, /entraId\.tenantId must be a string/i],
    ["Entra email list", { entraId: { allowedEmails: ["admin@example.com", 42] } }, /allowedEmails must be an array of strings/i],
  ];

  try {
    for (const [name, config, expectedError] of cases) {
      writeFileSync(configPath, JSON.stringify(config));
      const result = authenticateAdminRequest(new Headers(), {
        NODE_ENV: "development",
        WORKABLE_ADMIN_CONFIG_PATH: configPath,
      });

      assert.equal(result.ok, false, name);
      assert.equal(result.status, 503, name);
      assert.match(result.error, expectedError, name);
    }
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test("production responses do not disclose a missing config file path", () => {
  const missingPath = join(tmpdir(), "workable-admin-sensitive", "missing.json");
  const result = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_PATH: missingPath,
  });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
  assert.equal(result.error, "Workable admin UI configuration could not be loaded.");
  assert.doesNotMatch(result.error, /workable-admin-sensitive|missing\.json/);

  const repeated = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_PATH: missingPath,
  });
  assert.equal(repeated.ok, false);
  assert.equal(repeated.error, result.error);
});

test("production responses do not disclose a malformed config file path", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-sensitive-"));
  const configPath = join(temp, "sensitive-config-name.json");
  writeFileSync(configPath, "{not-json");

  try {
    const result = authenticateAdminRequest(new Headers(), {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_PATH: configPath,
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 503);
    assert.equal(result.error, "Workable admin UI configuration could not be loaded.");
    assert.doesNotMatch(result.error, /workable-admin-sensitive|sensitive-config-name/);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test("development config errors retain their diagnostic file path", () => {
  const missingPath = join(tmpdir(), "workable-admin-development", "missing.json");
  const result = authenticateAdminRequest(new Headers(), {
    NODE_ENV: "development",
    WORKABLE_ADMIN_CONFIG_PATH: missingPath,
  });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
  assert.match(result.error, /workable-admin-development/);
  assert.match(result.error, /missing\.json/);
});

test("server-only local config rejects non-boolean Basic enablement", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-"));
  const configPath = join(temp, "workable-admin.config.local.json");
  writeFileSync(
    configPath,
    JSON.stringify({
      authProvider: "basic",
      basicAuth: {
        enabled: "yes",
        username: "admin",
        password: "correct horse battery staple",
      },
    })
  );

  try {
    const authentication = authenticateAdminRequest(new Headers(), {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_PATH: configPath,
    });

    assert.equal(authentication.ok, false);
    assert.equal(authentication.status, 503);
    assert.match(authentication.error, /basicAuth\.enabled must be a boolean/i);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test("session signing secrets shorter than 32 bytes fail configuration closed", () => {
  const authentication = authenticateAdminRequest(new Headers(), secureEnv({
    WORKABLE_ADMIN_UI_SESSION_SECRET: "too-short",
  }));

  assert.equal(authentication.ok, false);
  assert.equal(authentication.status, 503);
  assert.match(authentication.error, /at least 32 UTF-8 bytes/i);
});

test("production session cookie configuration requires an unambiguous __Host cookie name", () => {
  for (const name of ["workable_admin_session", "bad; Domain=example.com"]) {
    const result = authenticateAdminRequest(new Headers(), secureEnv({
      WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME: name,
    }));
    assert.equal(result.ok, false);
    assert.equal(result.status, 503);
    assert.match(result.error, /sessionCookieName/);
  }
});

test("session cleanup falls back to safe cookie names when configuration is malformed", () => {
  assert.match(createExpiredAdminSessionCookie(secureEnv({
    WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME: "bad; Domain=example.com",
  })), /^__Host-workable_admin_session=/);
  assert.match(createExpiredAdminSessionCookie({
    ...secureEnv({ WORKABLE_ADMIN_UI_SESSION_COOKIE_NAME: "bad; name" }),
    NODE_ENV: "development",
  }), /^workable_admin_session=/);
});

test("explicit malformed numeric security settings fail configuration closed", () => {
  for (const [name, value] of [
    ["WORKABLE_ADMIN_UI_SESSION_MAX_AGE_SECONDS", "60seconds"],
    ["WORKABLE_ADMIN_UI_SESSION_ABSOLUTE_MAX_AGE_SECONDS", "0"],
    ["WORKABLE_ADMIN_UI_MAX_BODY_BYTES", "-1"],
    ["WORKABLE_ADMIN_UI_MAX_BODY_BYTES", "999999999999999999999999"],
  ]) {
    const result = authenticateAdminRequest(new Headers(), secureEnv({ [name]: value }));
    assert.equal(result.ok, false, name);
    assert.equal(result.status, 503, name);
    assert.match(result.error, /positive (safe )?integer/i, name);
  }
});

test("malformed numeric security settings in the config file fail closed", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-"));
  const configPath = join(temp, "workable-admin.config.local.json");
  writeFileSync(configPath, JSON.stringify({
    authProvider: "basic",
    basicAuth: {
      enabled: true,
      username: "admin",
      password: "correct horse battery staple",
    },
    sessionSecret: "replace-with-a-different-long-random-secret",
    sessionMaxAgeSeconds: "not-a-number",
  }));

  try {
    const result = authenticateAdminRequest(new Headers(), {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_PATH: configPath,
    });
    assert.equal(result.ok, false);
    assert.equal(result.status, 503);
    assert.match(result.error, /SESSION_MAX_AGE_SECONDS must be a positive safe integer/i);
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
});

test("malformed Entra target API JSON fails configuration closed", () => {
  const malformedJson = authenticateAdminRequest(new Headers(), entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: "{not-json",
  }));
  const wrongShape = authenticateAdminRequest(new Headers(), entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify({
      apiUrl: "https://workable.example.com/workable",
      scope: "api://client/workable.access",
    }),
  }));
  const malformedEntry = authenticateAdminRequest(new Headers(), entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{ apiUrl: 42 }]),
  }));
  const nonObjectEntry = authenticateAdminRequest(new Headers(), entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([null]),
  }));
  const emptyEntry = authenticateAdminRequest(new Headers(), entraEnv({
    WORKABLE_ADMIN_ENTRA_TARGET_APIS_JSON: JSON.stringify([{ apiUrl: " ", scope: "scope" }]),
  }));

  for (const result of [
    malformedJson,
    wrongShape,
    malformedEntry,
    nonObjectEntry,
    emptyEntry,
  ]) {
    assert.equal(result.ok, false);
    assert.equal(result.status, 503);
  }
});

test("malformed file target API configuration cannot be hidden by normalization", () => {
  const temp = mkdtempSync(join(tmpdir(), "workable-admin-"));
  const configPath = join(temp, "workable-admin.config.local.json");
  writeFileSync(configPath, JSON.stringify({
    authProvider: "entra",
    entraId: {
      tenantId: "tenant-id",
      clientId: "client-id",
      targetApis: { apiUrl: "https://workable.example.com/workable" },
    },
    sessionSecret: "replace-with-a-different-long-random-secret",
  }));

  try {
    const result = authenticateAdminRequest(new Headers(), {
      NODE_ENV: "production",
      WORKABLE_ADMIN_CONFIG_PATH: configPath,
    });
    assert.equal(result.ok, false);
    assert.equal(result.status, 503);
    assert.match(result.error, /targetApis must be an array/i);
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

function entraCallbackRequest(stateCookie: string) {
  return new Request(
    "https://admin.example.com/api/auth/entra/callback?state=forged-state&code=forged-code",
    {
      headers: {
        cookie: [
          `__Host-workable_admin_entra_state=${stateCookie}`,
          "__Host-workable_admin_entra_nonce=forged-nonce",
          "__Host-workable_admin_entra_verifier=forged-verifier",
        ].join("; "),
      },
    }
  );
}

function assertAuthenticationFailureStatus(
  result: ReturnType<typeof verifyAdminCredentials>,
  expectedStatus: number
) {
  assert.equal(result.ok, false);
  if (result.ok) {
    assert.fail("Expected authentication to fail.");
  }
  assert.equal(result.status, expectedStatus);
}

function secureEnv(overrides: AdminSecurityEnvironment = {}): AdminSecurityEnvironment {
  return {
    NODE_ENV: "production",
    WORKABLE_ADMIN_CONFIG_DISABLED: "true",
    WORKABLE_API_URL: "https://workable.example.com/workable",
    WORKABLE_ADMIN_UI_BASIC_AUTH_ENABLED: "true",
    WORKABLE_ADMIN_UI_USERNAME: "admin",
    WORKABLE_ADMIN_UI_PASSWORD: "correct horse battery staple",
    WORKABLE_ADMIN_UI_SESSION_SECRET: "basic-test-session-secret-that-is-at-least-32-bytes",
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

function createSignedInvalidLogoutValue(env: AdminSecurityEnvironment) {
  const payload = Buffer.from("{").toString("base64url");
  return `${payload}.${signAdminValue(
    payload,
    env.WORKABLE_ADMIN_UI_SESSION_SECRET as string
  )}`;
}

function signedEntraStateValue(
  state: string,
  startedAt = Date.now(),
  logoutGeneration = "initial"
) {
  const value = `${state}.${startedAt}.${logoutGeneration}`;
  return `${value}.${signAdminValue(
    value,
    "replace-with-a-different-long-random-secret"
  )}`;
}

function readLogoutGeneration(cookiePair: string) {
  const encodedValue = cookiePair.slice(cookiePair.indexOf("=") + 1);
  const payload = decodeURIComponent(encodedValue).split(".")[0] ?? "";
  return (JSON.parse(Buffer.from(payload, "base64url").toString("utf8")) as {
    generation: string;
  }).generation;
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
  const sessionCookie = createAdminSessionCookie(
    "admin",
    request,
    env,
    "entra",
    TEST_ENTRA_SUBJECT
  );
  assert.equal(sessionCookie.ok, true);
  if (!sessionCookie.ok) {
    throw new Error("Expected Entra session cookie.");
  }

  const tokenCookies = createEntraTargetTokenCookieHeaders(
    tokens,
    request,
    getAdminSecuritySettings(env),
    sessionCookie.identity
  );

  return [
    sessionCookie.header,
    ...tokenCookies,
  ]
    .map((header) => header.split(";")[0] ?? "")
    .filter(Boolean)
    .join("; ");
}
