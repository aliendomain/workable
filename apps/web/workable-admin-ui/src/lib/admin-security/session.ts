import {
  base64UrlDecode,
  base64UrlEncode,
  constantTimeEquals,
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
  logoutGeneration?: string;
};

export type AdminSessionReadResult = {
  identity: AdminSessionIdentity | null;
  expiresAt?: number;
  absoluteExpiresAt?: number;
  sessionId?: string;
  sessionStartedAt?: number;
  logoutGeneration?: string;
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
  logoutGeneration: string;
};

const SESSION_RENEWAL_WINDOW_SECONDS = 15 * 60;
const SECURE_LOGOUT_COOKIE_NAME = "__Host-workable_admin_logout";
const DEVELOPMENT_LOGOUT_COOKIE_NAME = "workable_admin_logout";
const INITIAL_LOGOUT_GENERATION = "initial";

export function createSignedAdminSessionCookie(
  identity: AdminSessionIdentity,
  request: Request,
  settings: AdminSecuritySettings,
  existingSession?: Pick<
    AdminSessionReadResult,
    "absoluteExpiresAt" | "sessionId" | "sessionStartedAt" | "logoutGeneration"
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
  const currentLogoutGeneration = readAdminLogoutGeneration(request.headers, settings);
  if (currentLogoutGeneration === null) {
    return securityFailure(
      401,
      "Workable admin UI logout state is invalid. Sign in again."
    );
  }
  const logoutGeneration = existingSession?.logoutGeneration ??
    identity.logoutGeneration ??
    currentLogoutGeneration;
  if (!constantTimeEquals(logoutGeneration, currentLogoutGeneration)) {
    return securityFailure(
      401,
      "Workable admin UI sign-in was invalidated by logout. Sign in again."
    );
  }
  const absoluteExpiresAt = existingSession?.absoluteExpiresAt ??
    now + settings.sessionAbsoluteMaxAgeSeconds;
  const expiresAt = Math.min(now + settings.sessionMaxAgeSeconds, absoluteExpiresAt);
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
    logoutGeneration,
  }));
  const signature = sign(payload, secret);

  return {
    ok: true,
    identity: { ...identity, sessionId, sessionStartedAt, logoutGeneration },
    logoutHeader: logoutGeneration === INITIAL_LOGOUT_GENERATION
      ? undefined
      : serializeLogoutGenerationCookie(
          logoutGeneration,
          request,
          settings,
          absoluteExpiresAt - now
        ),
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

export function createLogoutTombstoneCookie(request: Request, settings: AdminSecuritySettings) {
  const secret = sessionSecret(settings);
  if (!secret) return createExpiredSessionCookie(settings);
  return serializeLogoutGenerationCookie(
    crypto.randomUUID(),
    request,
    settings,
    settings.sessionAbsoluteMaxAgeSeconds
  );
}

function serializeLogoutGenerationCookie(
  generation: string,
  request: Request,
  settings: AdminSecuritySettings,
  maxAgeSeconds: number
) {
  const secret = sessionSecret(settings)!;
  const payload = base64UrlEncode(JSON.stringify({ generation }));
  return serializeCookie(
    logoutCookieName(request, settings),
    `${payload}.${sign(payload, secret)}`,
    {
      maxAgeSeconds,
      secure: shouldSecureCookie(request, settings.isProduction),
    }
  );
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
      !isValidLogoutGeneration(parsed.logoutGeneration) ||
      typeof parsed.binding !== "string" ||
      parsed.iat > now + 300 ||
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

    const logoutGeneration = readAdminLogoutGeneration(headers, settings);
    if (logoutGeneration === null || !constantTimeEquals(
      parsed.logoutGeneration,
      logoutGeneration
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
      logoutGeneration: parsed.logoutGeneration,
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
      logoutGeneration: parsed.logoutGeneration,
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

export function readAdminLogoutGeneration(
  headers: Headers,
  settings: AdminSecuritySettings
) {
  const secureCookie = readUniqueCookie(
    headers.get("cookie"),
    SECURE_LOGOUT_COOKIE_NAME
  );
  if (secureCookie.duplicate) return null;
  if (secureCookie.ok) return parseLogoutGeneration(secureCookie.value, settings);
  if (settings.isProduction) return INITIAL_LOGOUT_GENERATION;

  const developmentCookie = readUniqueCookie(
    headers.get("cookie"),
    DEVELOPMENT_LOGOUT_COOKIE_NAME
  );
  if (!developmentCookie.ok) {
    return developmentCookie.duplicate ? null : INITIAL_LOGOUT_GENERATION;
  }
  return parseLogoutGeneration(developmentCookie.value, settings);
}

function parseLogoutGeneration(value: string, settings: AdminSecuritySettings) {
  try {
    const [payload, signature] = value.split(".");
    const secret = sessionSecret(settings);
    if (!payload || !signature || !secret ||
      !constantTimeEquals(signature, sign(payload, secret))) return null;
    const parsed = JSON.parse(base64UrlDecode(payload)) as { generation?: unknown };
    return isValidLogoutGeneration(parsed.generation) ? parsed.generation : null;
  } catch {
    return null;
  }
}

function isValidLogoutGeneration(value: unknown): value is string {
  return value === INITIAL_LOGOUT_GENERATION ||
    (typeof value === "string" &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value));
}

function logoutCookieName(request: Request, settings: AdminSecuritySettings) {
  return shouldSecureCookie(request, settings.isProduction)
    ? SECURE_LOGOUT_COOKIE_NAME
    : DEVELOPMENT_LOGOUT_COOKIE_NAME;
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
    ? [settings.userName ?? "", settings.password ?? ""]
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
  return settings.sessionSecret ??
    (settings.authProvider === "basic" ? settings.password : undefined);
}
