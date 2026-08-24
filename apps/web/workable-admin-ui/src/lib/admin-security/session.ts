import {
  base64UrlDecode,
  base64UrlEncode,
  constantTimeEquals,
  deriveBasicCredentialBinding,
  sign,
} from "./crypto.ts";
import {
  readUniqueCookie,
  serializeCookie,
  serializeExpiredCookie,
  shouldSecureCookie,
} from "./cookies.ts";
import type { AdminSecuritySettings } from "./config.ts";
import type {
  AdminAuthProvider,
  AdminSessionCookieResult,
} from "./types.ts";
import { securityFailure, serviceUnavailable } from "./types.ts";

export type AdminSessionIdentity = {
  name: string;
  provider: AdminAuthProvider;
  email?: string;
  entraSubject?: string;
  sessionId?: string;
  sessionStartedAt?: number;
  logoutTombstones?: string[];
};

export type AdminSessionReadResult = {
  identity: AdminSessionIdentity | null;
  expiresAt?: number;
  absoluteExpiresAt?: number;
  sessionId?: string;
  sessionStartedAt?: number;
  logoutTombstones?: string[];
  hadCookie: boolean;
  shouldClear: boolean;
};

type AdminSessionPayload = {
  sub: string;
  provider: AdminAuthProvider;
  email?: string;
  entraSubject?: string;
  iat: number;
  exp: number;
  absoluteExp: number;
  binding: string;
  sid: string;
  startedAt: number;
  logoutTombstones: string[];
};

const SESSION_RENEWAL_WINDOW_SECONDS = 15 * 60;
const SESSION_CLOCK_SKEW_SECONDS = 5 * 60;
const SECURE_LOGOUT_COOKIE_PREFIX = "__Host-workable_admin_logout_";
const DEVELOPMENT_LOGOUT_COOKIE_PREFIX = "workable_admin_logout_";
const MAXIMUM_ACTIVE_LOGOUT_TOMBSTONES = 8;
const MAXIMUM_LOGOUT_COOKIE_CLEANUP = 32;
const LOGOUT_TOMBSTONE_SIGNATURE_PREFIX = "workable.admin.logout.tombstone.v1:";
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function createSignedAdminSessionCookie(
  identity: AdminSessionIdentity,
  request: Request,
  settings: AdminSecuritySettings,
  existingSession?: Pick<
    AdminSessionReadResult,
    "absoluteExpiresAt" | "sessionId" | "sessionStartedAt" | "logoutTombstones"
  >
): AdminSessionCookieResult {
  const secret = sessionSecret(settings);
  if (!secret) {
    return serviceUnavailable(
      "Workable admin UI session signing is not configured."
    );
  }

  if (identity.provider === "entra" && !identity.entraSubject?.trim()) {
    return serviceUnavailable(
      "Microsoft Entra ID session identity is missing its stable subject."
    );
  }

  const now = Math.floor(Date.now() / 1000);
  const sessionId = existingSession?.sessionId ?? identity.sessionId ?? crypto.randomUUID();
  const sessionStartedAt = existingSession?.sessionStartedAt ?? identity.sessionStartedAt ?? Date.now();
  const currentLogoutState = readAdminLogoutTombstones(request.headers, settings);
  if (!currentLogoutState.ok) {
    return securityFailure(
      401,
      "Workable admin UI logout state is invalid. Sign in again."
    );
  }
  const logoutTombstones = existingSession?.logoutTombstones ??
    identity.logoutTombstones ??
    currentLogoutState.tombstones;
  if (!isValidLogoutTombstoneSnapshot(logoutTombstones) ||
      !doesLogoutSnapshotCover(logoutTombstones, currentLogoutState.tombstones)) {
    return securityFailure(
      401,
      "Workable admin UI sign-in was invalidated by logout. Sign in again."
    );
  }
  const absoluteExpiresAt = existingSession?.absoluteExpiresAt ??
    Math.floor(sessionStartedAt / 1000) + settings.sessionAbsoluteMaxAgeSeconds;
  const expiresAt = Math.min(now + settings.sessionMaxAgeSeconds, absoluteExpiresAt);
  if (expiresAt <= now) {
    return securityFailure(
      401,
      "Workable admin UI sign-in expired before it completed. Sign in again."
    );
  }
  const payload = base64UrlEncode(JSON.stringify({
    sub: identity.name,
    provider: identity.provider,
    email: identity.email,
    entraSubject: identity.entraSubject,
    iat: now,
    exp: expiresAt,
    absoluteExp: absoluteExpiresAt,
    binding: createSessionConfigurationBinding(identity, settings, secret),
    sid: sessionId,
    startedAt: sessionStartedAt,
    logoutTombstones,
  }));
  const signature = sign(payload, secret);

  return {
    ok: true,
    identity: {
      ...identity,
      sessionId,
      sessionStartedAt,
      logoutTombstones: [...logoutTombstones],
    },
    header: serializeCookie(
      settings.sessionCookieName,
      `${payload}.${signature}`,
      {
        maxAgeSeconds: expiresAt - now,
        secure: shouldSecureCookie(request, settings.isProduction),
      }
    ),
  };
}

export function createLogoutTombstoneCookies(
  request: Request,
  settings: AdminSecuritySettings
) {
  const secret = sessionSecret(settings);
  if (!secret) return [createExpiredSessionCookie(settings)];

  const secure = shouldSecureCookie(request, settings.isProduction);
  const prefix = secure
    ? SECURE_LOGOUT_COOKIE_PREFIX
    : DEVELOPMENT_LOGOUT_COOKIE_PREFIX;
  const tombstone = crypto.randomUUID();
  const name = `${prefix}${tombstone}`;
  const signature = signLogoutTombstone(tombstone, secret);
  const cookies = [serializeCookie(
    name,
    signature,
    {
      maxAgeSeconds: Math.min(
        Number.MAX_SAFE_INTEGER,
        settings.sessionAbsoluteMaxAgeSeconds + SESSION_CLOCK_SKEW_SECONDS
      ),
      secure,
    }
  )];

  for (const staleName of readLogoutCookieNames(
    request.headers.get("cookie"),
    prefix,
    MAXIMUM_LOGOUT_COOKIE_CLEANUP
  )) {
    if (staleName !== name) {
      cookies.push(serializeExpiredCookie(staleName));
    }
  }
  return cookies;
}

function signLogoutTombstone(tombstone: string, secret: string) {
  return sign(`${LOGOUT_TOMBSTONE_SIGNATURE_PREFIX}${tombstone}`, secret);
}

export function createExpiredSessionCookie(settings: AdminSecuritySettings) {
  const configuredName = settings.sessionCookieName;
  const nameIsSafe = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(configuredName) &&
    (!settings.isProduction || configuredName.startsWith("__Host-"));
  return serializeExpiredCookie(nameIsSafe
    ? configuredName
    : settings.isProduction
      ? "__Host-workable_admin_session"
      : "workable_admin_session");
}

export function readAdminSession(
  headers: Headers,
  settings: AdminSecuritySettings
): AdminSessionIdentity | null {
  return readAdminSessionState(headers, settings).identity;
}

export function readAdminSessionState(
  headers: Headers,
  settings: AdminSecuritySettings
): AdminSessionReadResult {
  const cookie = readUniqueCookie(headers.get("cookie"), settings.sessionCookieName);
  if (!cookie.ok) {
    return {
      identity: null,
      hadCookie: cookie.duplicate,
      shouldClear: cookie.duplicate,
    };
  }
  const value = cookie.value;
  if (!value) {
    return {
      identity: null,
      hadCookie: false,
      shouldClear: false,
    };
  }

  const secret = sessionSecret(settings);
  if (!secret) {
    return {
      identity: null,
      hadCookie: true,
      shouldClear: true,
    };
  }

  const [payload, signature] = value.split(".");
  if (!payload || !signature || !constantTimeEquals(signature, sign(payload, secret))) {
    return {
      identity: null,
      hadCookie: true,
      shouldClear: true,
    };
  }

  try {
    const parsed = JSON.parse(base64UrlDecode(payload)) as AdminSessionPayload;
    const provider = parsed.provider;
    const now = Math.floor(Date.now() / 1000);
    if (
      typeof parsed.sub !== "string" ||
      !parsed.sub.trim() ||
      (parsed.email !== undefined && typeof parsed.email !== "string") ||
      (parsed.entraSubject !== undefined && typeof parsed.entraSubject !== "string") ||
      (provider === "entra" && !parsed.entraSubject?.trim()) ||
      !Number.isSafeInteger(parsed.iat) ||
      !Number.isSafeInteger(parsed.exp) ||
      !Number.isSafeInteger(parsed.absoluteExp) ||
      typeof parsed.sid !== "string" || !parsed.sid.trim() ||
      !Number.isSafeInteger(parsed.startedAt) ||
      !isValidLogoutTombstoneSnapshot(parsed.logoutTombstones) ||
      typeof parsed.binding !== "string" ||
      parsed.iat > now + SESSION_CLOCK_SKEW_SECONDS ||
      parsed.exp <= now ||
      parsed.absoluteExp <= now ||
      parsed.exp > parsed.absoluteExp ||
      provider !== settings.authProvider
    ) {
      return {
        identity: null,
        hadCookie: true,
        shouldClear: true,
      };
    }

    const logoutState = readAdminLogoutTombstones(headers, settings);
    if (!logoutState.ok || !doesLogoutSnapshotCover(
      parsed.logoutTombstones,
      logoutState.tombstones
    )) {
      return { identity: null, hadCookie: true, shouldClear: true };
    }

    const identity = {
      name: parsed.sub,
      provider,
      email: parsed.email,
      entraSubject: parsed.entraSubject,
      sessionId: parsed.sid,
      sessionStartedAt: parsed.startedAt,
      logoutTombstones: [...parsed.logoutTombstones],
    };
    if (!constantTimeEquals(
      parsed.binding,
      createSessionConfigurationBinding(identity, settings, secret)
    )) {
      return {
        identity: null,
        hadCookie: true,
        shouldClear: true,
      };
    }

    return {
      identity,
      expiresAt: parsed.exp,
      absoluteExpiresAt: parsed.absoluteExp,
      sessionId: parsed.sid,
      sessionStartedAt: parsed.startedAt,
      logoutTombstones: [...parsed.logoutTombstones],
      hadCookie: true,
      shouldClear: false,
    };
  } catch {
    return {
      identity: null,
      hadCookie: true,
      shouldClear: true,
    };
  }
}

export function readAdminLogoutTombstones(
  headers: Headers,
  settings: AdminSecuritySettings
) {
  const cookieHeader = headers.get("cookie");
  const secureState = readLogoutTombstonesForPrefix(
    cookieHeader,
    SECURE_LOGOUT_COOKIE_PREFIX,
    settings
  );
  if (secureState.present || settings.isProduction) {
    return secureState.ok
      ? { ok: true as const, tombstones: secureState.tombstones }
      : { ok: false as const };
  }
  const developmentState = readLogoutTombstonesForPrefix(
    cookieHeader,
    DEVELOPMENT_LOGOUT_COOKIE_PREFIX,
    settings
  );
  return developmentState.ok
    ? { ok: true as const, tombstones: developmentState.tombstones }
    : { ok: false as const };
}

function readLogoutTombstonesForPrefix(
  cookieHeader: string | null,
  prefix: string,
  settings: AdminSecuritySettings
) {
  const tombstones: string[] = [];
  const names = new Set<string>();
  const identifiers = new Set<string>();
  let present = false;
  const secret = sessionSecret(settings);

  for (const pair of cookieHeader?.split(";") ?? []) {
    const separator = pair.indexOf("=");
    if (separator < 0) continue;
    const name = pair.slice(0, separator).trim();
    if (!name.startsWith(prefix)) continue;
    present = true;
    const tombstone = name.slice(prefix.length);
    const identifier = tombstone.toLowerCase();
    const rawValue = pair.slice(separator + 1).trim();
    let value: string;
    try {
      value = decodeURIComponent(rawValue);
    } catch {
      return { ok: false as const, present };
    }
    if (!UUID_PATTERN.test(tombstone) || names.has(name) || identifiers.has(identifier) || !secret ||
        !constantTimeEquals(value, signLogoutTombstone(tombstone, secret))) {
      return { ok: false as const, present };
    }
    names.add(name);
    identifiers.add(identifier);
    tombstones.push(identifier);
    if (tombstones.length > MAXIMUM_ACTIVE_LOGOUT_TOMBSTONES) {
      return { ok: false as const, present };
    }
  }

  tombstones.sort();
  return { ok: true as const, present, tombstones };
}

function readLogoutCookieNames(
  cookieHeader: string | null,
  prefix: string,
  maximum: number
) {
  const names = new Set<string>();
  for (const pair of cookieHeader?.split(";") ?? []) {
    const separator = pair.indexOf("=");
    if (separator < 0) continue;
    const name = pair.slice(0, separator).trim();
    if (!name.startsWith(prefix) ||
        name.length > prefix.length + 64 ||
        !/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name)) continue;
    names.add(name);
    if (names.size >= maximum) break;
  }
  return names;
}

export function isValidLogoutTombstoneSnapshot(
  value: unknown
): value is string[] {
  if (!Array.isArray(value) || value.length > MAXIMUM_ACTIVE_LOGOUT_TOMBSTONES) {
    return false;
  }
  let previous = "";
  for (const tombstone of value) {
    if (typeof tombstone !== "string" ||
        !UUID_PATTERN.test(tombstone) ||
        tombstone !== tombstone.toLowerCase() ||
        tombstone <= previous) return false;
    previous = tombstone;
  }
  return true;
}

export function doesLogoutSnapshotCover(
  snapshot: readonly string[],
  activeTombstones: readonly string[]
) {
  if (activeTombstones.length === 0) return true;
  let snapshotIndex = 0;
  for (const tombstone of activeTombstones) {
    while (snapshotIndex < snapshot.length &&
        (snapshot[snapshotIndex] as string) < tombstone) {
      snapshotIndex++;
    }
    if (snapshot[snapshotIndex] !== tombstone) return false;
  }
  return true;
}

export function shouldRenewAdminSession(
  expiresAt: number | undefined,
  absoluteExpiresAt: number | undefined,
  settings: AdminSecuritySettings
) {
  if (!expiresAt || !absoluteExpiresAt) {
    return false;
  }

  const now = Math.floor(Date.now() / 1000);
  if (absoluteExpiresAt <= now) {
    return false;
  }
  const remainingSeconds = expiresAt - now;
  const renewalWindow = Math.min(
    SESSION_RENEWAL_WINDOW_SECONDS,
    Math.max(60, Math.floor(settings.sessionMaxAgeSeconds / 4))
  );
  return remainingSeconds <= renewalWindow;
}

function createSessionConfigurationBinding(
  identity: AdminSessionIdentity,
  settings: AdminSecuritySettings,
  secret: string
) {
  const providerConfiguration = identity.provider === "basic"
    ? [
        settings.userName ?? "",
        deriveBasicCredentialBinding(
          settings.password ?? "",
          secret
        ),
      ]
    : [
        settings.entraId.tenantId ?? "",
        settings.entraId.clientId ?? "",
        settings.entraId.authorityHost,
      ];
  return sign(JSON.stringify([
    "workable.admin.session.binding.v1",
    identity.provider,
    ...providerConfiguration,
    settings.sessionMaxAgeSeconds,
    settings.sessionAbsoluteMaxAgeSeconds,
  ]), secret);
}

export function sessionSecret(settings: AdminSecuritySettings) {
  return settings.sessionSecret;
}
