import {
  base64UrlDecode,
  base64UrlEncode,
  constantTimeEquals,
  sign,
} from "./crypto.ts";
import {
  parseCookieHeader,
  serializeCookie,
  serializeExpiredCookie,
  shouldSecureCookie,
} from "./cookies.ts";
import type { AdminSecuritySettings } from "./config.ts";
import type {
  AdminAuthProvider,
  AdminSessionCookieResult,
} from "./types.ts";
import { serviceUnavailable } from "./types.ts";

export type AdminSessionIdentity = {
  name: string;
  provider: AdminAuthProvider;
  email?: string;
};

export type AdminSessionReadResult = {
  identity: AdminSessionIdentity | null;
  expiresAt?: number;
  hadCookie: boolean;
  shouldClear: boolean;
};

type AdminSessionPayload = {
  sub: string;
  provider?: AdminAuthProvider;
  email?: string;
  exp: number;
};

const SESSION_RENEWAL_WINDOW_SECONDS = 15 * 60;

export function createSignedAdminSessionCookie(
  identity: AdminSessionIdentity,
  request: Request,
  settings: AdminSecuritySettings
): AdminSessionCookieResult {
  const secret = sessionSecret(settings);
  if (!secret) {
    return serviceUnavailable(
      "Workable admin UI session signing is not configured."
    );
  }

  const expiresAt = Math.floor(Date.now() / 1000) + settings.sessionMaxAgeSeconds;
  const payload = base64UrlEncode(JSON.stringify({
    sub: identity.name,
    provider: identity.provider,
    email: identity.email,
    exp: expiresAt,
  }));
  const signature = sign(payload, secret);

  return {
    ok: true,
    header: serializeCookie(
      settings.sessionCookieName,
      `${payload}.${signature}`,
      {
        maxAgeSeconds: settings.sessionMaxAgeSeconds,
        secure: shouldSecureCookie(request, settings.isProduction),
      }
    ),
  };
}

export function createExpiredSessionCookie(settings: AdminSecuritySettings) {
  return serializeExpiredCookie(settings.sessionCookieName);
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
  const value = parseCookieHeader(headers.get("cookie")).get(settings.sessionCookieName);
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
    const provider = parsed.provider ?? "basic";
    const now = Math.floor(Date.now() / 1000);
    if (
      !parsed.sub ||
      parsed.exp <= now ||
      provider !== settings.authProvider
    ) {
      return {
        identity: null,
        hadCookie: true,
        shouldClear: true,
      };
    }

    return {
      identity: {
        name: parsed.sub,
        provider,
        email: parsed.email,
      },
      expiresAt: parsed.exp,
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

export function shouldRenewAdminSession(
  expiresAt: number | undefined,
  settings: AdminSecuritySettings
) {
  if (!expiresAt) {
    return false;
  }

  const now = Math.floor(Date.now() / 1000);
  const remainingSeconds = expiresAt - now;
  const renewalWindow = Math.min(
    SESSION_RENEWAL_WINDOW_SECONDS,
    Math.max(60, Math.floor(settings.sessionMaxAgeSeconds / 4))
  );
  return remainingSeconds <= renewalWindow;
}

export function sessionSecret(settings: AdminSecuritySettings) {
  return settings.sessionSecret ??
    (settings.authProvider === "basic" ? settings.password : undefined);
}
