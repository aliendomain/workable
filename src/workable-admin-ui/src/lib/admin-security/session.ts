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

type AdminSessionPayload = {
  sub: string;
  provider?: AdminAuthProvider;
  email?: string;
  exp: number;
};

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
  const value = parseCookieHeader(headers.get("cookie")).get(settings.sessionCookieName);
  if (!value) {
    return null;
  }

  const secret = sessionSecret(settings);
  if (!secret) {
    return null;
  }

  const [payload, signature] = value.split(".");
  if (!payload || !signature || !constantTimeEquals(signature, sign(payload, secret))) {
    return null;
  }

  try {
    const parsed = JSON.parse(base64UrlDecode(payload)) as AdminSessionPayload;
    const provider = parsed.provider ?? "basic";
    if (
      !parsed.sub ||
      parsed.exp <= Math.floor(Date.now() / 1000) ||
      provider !== settings.authProvider
    ) {
      return null;
    }

    return {
      name: parsed.sub,
      provider,
      email: parsed.email,
    };
  } catch {
    return null;
  }
}

export function sessionSecret(settings: AdminSecuritySettings) {
  return settings.sessionSecret ??
    (settings.authProvider === "basic" ? settings.password : undefined);
}
